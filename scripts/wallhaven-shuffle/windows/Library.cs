// The library: what this PC shuffles (any mix of the toplist, Favourites, playlists and folders of your own), the
// playlists and Hidden (shared with the TV and the remote when synced), and the sync itself.
//
// Playlists live in one document (library.json, or the service's copy once paired). Each one is also a folder of
// pictures (Favourites: Pictures\Wallhaven Favourites; playlists: PlaylistsRoot\<name>), kept in step both ways:
// a wallpaper added on the TV shows up in its folder (linked to the downloaded original, or downloaded first), and a
// picture dropped into a folder in Explorer joins the playlist. Only what was synced before is ever removed from a
// folder, and then to the Recycle Bin.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace WallhavenShuffle
{
    partial class TrayApp
    {
        // ------------------------------------------------------------------------- the library's state

        SyncDoc doc = new SyncDoc();
        readonly List<Dictionary<string, object>> outbox = new List<Dictionary<string, object>>();   // changes not yet at the service
        Dictionary<string, string> folderNames = new Dictionary<string, string>();                   // playlist id -> its folder's name last time
        Dictionary<string, HashSet<string>> folderFiles = new Dictionary<string, HashSet<string>>();  // playlist id -> wallpapers its folder had last time
        string syncToken = "", syncId = "", syncName = "", stHash = "";
        int syncV = -1, cmdSeq;
        bool settingsDirty, syncLive, syncReachable = true;
        string syncError = "";
        readonly Dictionary<string, Tuple<DateTime, List<Wall>>> folderCache = new Dictionary<string, Tuple<DateTime, List<Wall>>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> hiddenLocal;          // "Never show this one" for pictures of your own (not Wallhaven: not synced)

        string LibraryFile { get { return Path.Combine(Settings.Dir, "library.json"); } }

        void LoadLibrary()
        {
            var root = File.Exists(LibraryFile) ? Json.Parse(SafeRead(LibraryFile)) : null;
            object v;
            if (root != null)
            {
                doc = SyncDoc.FromDict(root.TryGetValue("doc", out v) ? v as Dictionary<string, object> : null);
                if (root.TryGetValue("outbox", out v) && v is object[]) foreach (var o in (object[])v) if (o is Dictionary<string, object>) outbox.Add((Dictionary<string, object>)o);
                if (root.TryGetValue("folders", out v) && v is Dictionary<string, object>) foreach (var kv in (Dictionary<string, object>)v) folderNames[kv.Key] = Convert.ToString(kv.Value);
                if (root.TryGetValue("files", out v) && v is Dictionary<string, object>)
                    foreach (var kv in (Dictionary<string, object>)v) folderFiles[kv.Key] = new HashSet<string>(((kv.Value as object[]) ?? new object[0]).Select(Convert.ToString));
                var s = root.TryGetValue("sync", out v) ? v as Dictionary<string, object> : null;
                if (s != null)
                {
                    Func<string, string> str = k => { object x; return s.TryGetValue(k, out x) && x != null ? Convert.ToString(x) : ""; };
                    syncToken = str("token"); syncId = str("id"); syncName = str("name"); stHash = str("st");
                    int n;
                    syncV = int.TryParse(str("v"), out n) ? n : -1;
                    cmdSeq = int.TryParse(str("cs"), out n) ? n : 0;
                    settingsDirty = str("dirty") == "True";
                }
            }
            // Before 2.2, "Never show this one" was blocked.txt: Wallhaven ones move to Hidden (synced), the rest stay here.
            var old = ReadList("blocked.txt").ToList();
            hiddenLocal = new HashSet<string>(old.Where(id => id.IndexOf('\\') >= 0), StringComparer.OrdinalIgnoreCase);
            var moved = old.Where(SyncDoc.IsWallId).Where(id => !doc.Find("hidden").Has(id)).ToList();
            foreach (var id in moved) doc.Find("hidden").Items.Add(new SyncItem { Id = id, At = 0 });
            if (moved.Count > 0) { Log("moved " + moved.Count + " hidden wallpapers to the Hidden playlist"); WriteList("blocked.txt", hiddenLocal); SaveLibrary(); }
        }

        static string SafeRead(string path) { try { return File.ReadAllText(path); } catch (IOException) { return ""; } }

        void SaveLibrary()
        {
            var root = new Dictionary<string, object> {
                { "doc", doc.ToDict() },
                { "outbox", outbox.Cast<object>().ToArray() },
                { "folders", folderNames.ToDictionary(kv => kv.Key, kv => (object)kv.Value) },
                { "files", folderFiles.ToDictionary(kv => kv.Key, kv => (object)kv.Value.ToArray()) },
                { "sync", new Dictionary<string, object> { { "token", syncToken }, { "id", syncId }, { "name", syncName }, { "v", syncV }, { "cs", cmdSeq }, { "dirty", settingsDirty.ToString() }, { "st", stHash } } } };
            WriteFileSafely(LibraryFile, Json.Write(root));
        }

        // A change to the playlists, made here: applied at once (and to the folders by the caller), and sent to the
        // service at the next check-in. Returns why it couldn't be made, or null.
        string Change(Dictionary<string, object> op)
        {
            string why = doc.Apply(op);
            if (why != null) { Log("couldn't change the playlists: " + why); return why; }
            if (Paired) { outbox.Add(op); KickSync(); }
            SaveLibrary();
            folderCache.Clear();
            Changed();
            return null;
        }

        // ------------------------------------------------------------------------- sources

        // Something to shuffle: the toplist, Favourites, a playlist, or a folder of your own (used where it is).
        public class SourceInfo
        {
            public string Key, Name, Folder, Glyph;
            public bool Own;                 // a playlist: it can be renamed and deleted
            public int Count = -1;           // wallpapers in it, when known
        }

        public string PlaylistsRoot { get { return settings.PlaylistsFolder; } }
        public string FavouritesFolder { get { return favouritesDir; } }

        public List<SourceInfo> Sources()
        {
            var list = new List<SourceInfo>
            {
                new SourceInfo { Key = "toplist", Name = "Toplist", Glyph = Glyphs.Globe, Count = walls.Count },
                new SourceInfo { Key = "fav", Name = "Favourites", Folder = favouritesDir, Glyph = Glyphs.Heart, Count = doc.Find("fav").Items.Count }
            };
            foreach (var p in doc.Own)
                list.Add(new SourceInfo { Key = p.Id, Name = p.Name, Folder = Path.Combine(PlaylistsRoot, p.Name), Glyph = Glyphs.Library, Own = true, Count = p.Items.Count });
            foreach (var f in folders) list.Add(new SourceInfo { Key = "folder:" + f, Name = FolderLabel(f), Folder = f, Glyph = Glyphs.Pictures });
            return list;
        }

        public SourceInfo SourceByKey(string key) { return Sources().FirstOrDefault(x => x.Key == key); }
        public bool IsOn(string key) { return settings.Sources.Contains(key); }
        bool ToplistOn { get { return IsOn("toplist"); } }
        bool OnlyToplist { get { return settings.Sources.Count == 1 && ToplistOn; } }

        public string SourceFolder(string key) { var s = SourceByKey(key); return s == null ? null : s.Folder; }

        public string SourceName(string key) { var s = SourceByKey(key); return s != null ? s.Name : key.StartsWith("folder:") ? FolderLabel(key.Substring(7)) : key; }

        static string FolderLabel(string path)
        {
            string name = Path.GetFileName(path.TrimEnd('\\'));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        // "Toplist, Cozy evenings" / "Toplist + 2 more" / "Nothing".
        public string SourcesSummary
        {
            get
            {
                var names = settings.Sources.Select(k => SourceByKey(k)).Where(s => s != null).Select(s => s.Name).ToList();
                if (names.Count == 0) return "Nothing yet";
                return names.Count <= 2 ? string.Join(", ", names) : names[0] + " + " + (names.Count - 1) + " more";
            }
        }

        // There's something to shuffle: the toplist with a service address, or a source whose folder exists.
        public bool HasSource
        {
            get
            {
                foreach (var k in settings.Sources)
                {
                    if (k == "toplist") { if (!string.IsNullOrEmpty(settings.Server)) return true; continue; }
                    string f = SourceFolder(k);
                    if (f != null && (Directory.Exists(f) || k == "fav" || !k.StartsWith("folder:"))) return true;
                }
                return false;
            }
        }

        // Each mix of sources keeps its own round (the toplist alone keeps seen.txt, as before).
        string RoundFile
        {
            get
            {
                if (OnlyToplist) return "seen.txt";
                string key = string.Join("|", settings.Sources.OrderBy(k => k, StringComparer.Ordinal));
                uint h = 2166136261;
                foreach (char c in key) h = (h ^ c) * 16777619;
                return "seen-" + h.ToString("x8") + ".txt";
            }
        }

        // Turns a source on or off. Its new pool shows one wallpaper right away (as picking a source always did).
        public void ToggleSource(string key, bool on)
        {
            if (IsOn(key) == on) return;
            var next = settings.Sources.ToList();
            if (on) next.Add(key); else next.Remove(key);
            SetSources(next, true);
        }

        void SetSources(List<string> next, bool local)
        {
            if (next.SequenceEqual(settings.Sources)) return;
            if (next.Contains("fav")) try { Directory.CreateDirectory(favouritesDir); } catch (Exception) { }
            WriteList(RoundFile, rot.Round);
            settings.Sources = next;
            settings.Save();
            rot.LoadRound(ReadList(RoundFile));
            folderCache.Clear(); prepared = null;
            Log("shuffling from " + (next.Count == 0 ? "nothing" : string.Join(", ", next.Select(SourceName))));
            if (local) MarkSettingsDirty();
            Changed();
            var pool = Pool();
            if (current != null && pool.Any(w => w.Id == current.Id) && !local) return;   // what's on screen still belongs
            if (busy) nextChange = DateTime.Now;
            else if (HasSource && pool.Count > 0 && !settings.Paused && !IsKept) Next("source");
        }

        // ------------------------------------------------------------------------- the pool

        // What's being shuffled: every source that's on, mixed; Hidden left out; each wallpaper once (a downloaded
        // picture in a playlist is preferred to the same one from the toplist).
        List<Wall> Pool()
        {
            var hidden = doc.Hidden;
            var byKey = new Dictionary<string, Wall>(StringComparer.OrdinalIgnoreCase);
            int maxw = settings.Filters.MaxWidth;
            foreach (var key in settings.Sources)
            {
                if (key != "toplist")
                {
                    string folder = SourceFolder(key);
                    if (folder == null) continue;
                    foreach (var w in FolderPool(folder))
                    {
                        string wid = w.WallhavenId;
                        if ((wid != null && hidden.Contains(wid)) || hiddenLocal.Contains(w.Id)) continue;
                        byKey[wid ?? w.Id] = w;
                    }
                }
            }
            if (ToplistOn)
                foreach (var w in walls)
                    if (!hidden.Contains(w.Id) && !byKey.ContainsKey(w.Id) && (maxw == 0 || WidthOf(w) <= maxw)) byKey[w.Id] = w;
            return byKey.Values.ToList();
        }

        static int WidthOf(Wall w) { int x; var r = w.Resolution ?? ""; int i = r.IndexOf('x'); return i > 0 && int.TryParse(r.Substring(0, i), out x) ? x : 0; }

        // The pictures in a folder (top level; hidden files and unfinished downloads left out). Read again every 20
        // seconds, so pictures copied in by hand show up, and straight after the app changes something.
        List<Wall> FolderPool(string folder)
        {
            Tuple<DateTime, List<Wall>> hit;
            if (folderCache.TryGetValue(folder, out hit) && DateTime.Now - hit.Item1 < TimeSpan.FromSeconds(20)) return hit.Item2;
            var list = new List<Wall>();
            try
            {
                if (Directory.Exists(folder))
                {
                    var byId = new Dictionary<string, Wall>();
                    foreach (var w in walls) byId[w.Id] = w;
                    foreach (var f in new DirectoryInfo(folder).EnumerateFiles())
                    {
                        if (!Wall.IsImageFile(f.Name) || (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || f.Length == 0) continue;
                        string name = Path.GetFileNameWithoutExtension(f.Name);
                        Wall m;
                        if (!byId.TryGetValue(name, out m)) m = ItemWall(name);
                        list.Add(Wall.Local(f.FullName, m));
                    }
                }
            }
            catch (Exception ex) { Log("couldn't read " + folder + ": " + ex.Message); }
            list.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            folderCache[folder] = Tuple.Create(DateTime.Now, list);
            return list;
        }

        // A wallpaper's details from the playlists (for a picture that has left the toplist), or null.
        Wall ItemWall(string id)
        {
            if (!SyncDoc.IsWallId(id)) return null;
            var it = doc.Playlists.SelectMany(p => p.Items).FirstOrDefault(x => x.Id == id && x.Res.Length > 0);
            return it == null ? null : new Wall { Id = it.Id, Image = it.Image, Page = it.Page, Resolution = it.Res, Favorites = it.Fav, Extension = "." + it.Ext };
        }

        // The playlists' entry for a wallpaper (Wallhaven ones only), or null.
        SyncItem ItemOf(Wall w)
        {
            if (w == null) return null;
            string id = w.WallhavenId;
            if (id == null || !SyncDoc.IsWallId(id)) return null;
            string ext = (w.Extension ?? (w.IsLocal ? Path.GetExtension(w.LocalPath) : ".jpg")).TrimStart('.');
            var it = SyncDoc.MakeItem(id, ext, w.Resolution, Math.Max(0, w.Favorites));
            var known = doc.Playlists.SelectMany(p => p.Items).FirstOrDefault(x => x.Id == id);
            if (known != null && it.Res.Length == 0) { it.Res = known.Res; it.Fav = known.Fav; }
            return it;
        }

        // ------------------------------------------------------------------------- favourites, playlists, hidden

        public bool InPlaylist(SourceInfo p, Wall w)
        {
            if (p == null || w == null) return false;
            var item = ItemOf(w);
            var pl = doc.Find(p.Key);
            if (item != null && pl != null) return pl.Has(item.Id);
            return p.Folder != null && (w.IsLocal && SameFolder(Path.GetDirectoryName(w.LocalPath), p.Folder) || File.Exists(Path.Combine(p.Folder, w.FileName)));
        }

        public bool IsFavourite { get { return current != null && InPlaylist(SourceByKey("fav"), current); } }

        static bool SameFolder(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        public void Favourite() { TogglePlaylist(SourceByKey("fav")); }

        // Adds the one on screen to a playlist (Favourites too), or takes it out if it's in it. Its folder follows at
        // once: a link to the same file when on the same drive (no extra space), a copy otherwise; taken out, to the
        // Recycle Bin. A picture of your own (not from Wallhaven) only goes into the folder, and stays on this PC.
        public void TogglePlaylist(SourceInfo p)
        {
            if (current == null || p == null || p.Folder == null) return;
            var w = current;
            bool inIt = InPlaylist(p, w);
            var item = ItemOf(w);
            var pl = doc.Find(p.Key);
            try
            {
                Directory.CreateDirectory(p.Folder);
                string dst = Path.Combine(p.Folder, item != null ? item.Id + "." + item.Ext : w.FileName);
                bool shownFromIt = w.IsLocal && SameFolder(Path.GetDirectoryName(w.LocalPath), p.Folder);
                if (inIt)
                {
                    if (shownFromIt) ReleaseKeep("taken out of " + p.Name);   // its file is going
                    string file = shownFromIt ? w.LocalPath : File.Exists(dst) ? dst : FindInFolder(p.Folder, item != null ? item.Id : null);
                    if (file != null && File.Exists(file)) RemoveFile(file);
                    Log("removed " + Describe(w) + " from " + p.Name);
                }
                else if (!File.Exists(dst))
                    Log((Native.LinkOrCopy(FileOf(w), dst) ? "added (linked) " : "added (copied: another drive) ") + Describe(w) + " to " + p.Name);
            }
            catch (Exception ex) { Log(p.Name + ": " + ex.Message); }
            if (item != null && pl != null)
            {
                Change(inIt ? SyncDoc.Op("remove", "pl", pl.Id, "id", item.Id) : SyncDoc.Op("add", "pl", pl.Id, "item", SyncDoc.ItemDict(item)));
                Known(pl.Id, item.Id, !inIt);
            }
            folderCache.Clear();
            Changed();
        }

        // Records what a playlist's folder has now (so the next reconcile knows it was synced, not new).
        void Known(string plId, string id, bool present)
        {
            HashSet<string> set;
            if (!folderFiles.TryGetValue(plId, out set)) folderFiles[plId] = set = new HashSet<string>();
            if (present) set.Add(id); else set.Remove(id);
            SaveLibrary();
        }

        static string FindInFolder(string folder, string id)
        {
            if (id == null || !Directory.Exists(folder)) return null;
            return Directory.EnumerateFiles(folder, id + ".*").FirstOrDefault(f => Wall.IsImageFile(f) && Path.GetFileNameWithoutExtension(f).Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        static void RemoveFile(string path) { if (!Native.Recycle(path)) File.Delete(path); }

        public int HiddenCount { get { return doc.Find("hidden").Items.Count + hiddenLocal.Count; } }

        // "Never show this one": into Hidden (on every device), then the next one.
        public void Block()
        {
            if (current == null) return;
            ReleaseKeep("hidden");
            var item = ItemOf(current);
            if (item != null) Change(SyncDoc.Op("add", "pl", "hidden", "item", SyncDoc.ItemDict(item)));
            else { hiddenLocal.Add(current.Id); WriteList("blocked.txt", hiddenLocal); }
            Log("hid " + Describe(current));
            rot.Back = -1;   // straight to a new one, even while going back
            prepared = null;
            Next("hidden");
        }

        // Undoes every "Never show this one".
        public void UnhideAll()
        {
            Log("showing " + HiddenCount + " hidden wallpapers again");
            if (doc.Find("hidden").Items.Count > 0) Change(SyncDoc.Op("clear", "pl", "hidden"));
            hiddenLocal.Clear();
            WriteList("blocked.txt", hiddenLocal);
            prepared = null;
            Changed();
        }

        public string CheckPlaylistName(string name, string exceptId) { return doc.CheckName(name, exceptId); }

        // A new playlist (and its folder); addCurrent: the one on screen goes in first.
        public string CreatePlaylist(string name, bool addCurrent)
        {
            string why = doc.CheckName(name, null);
            if (why != null) return why;
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim();
            string id = SyncDoc.NewId();
            try { Directory.CreateDirectory(Path.Combine(PlaylistsRoot, name)); }
            catch (Exception ex) { return "Couldn't make its folder: " + ex.Message; }
            why = Change(SyncDoc.Op("create", "id", id, "name", name, "items", new object[0]));
            if (why != null) return why;
            folderNames[id] = name;
            folderFiles[id] = new HashSet<string>();
            SaveLibrary();
            Log("new playlist " + name);
            if (addCurrent && current != null) TogglePlaylist(SourceByKey(id));
            return null;
        }

        public string RenamePlaylist(SourceInfo p, string newName)
        {
            string why = doc.CheckName(newName, p.Key);
            if (why != null) return why;
            newName = System.Text.RegularExpressions.Regex.Replace(newName, @"\s+", " ").Trim();
            if (newName == p.Name) return null;
            string from = Path.Combine(PlaylistsRoot, p.Name), to = Path.Combine(PlaylistsRoot, newName);
            try { MoveFolder(from, to); }
            catch (Exception ex) { return "Couldn't rename its folder: " + ex.Message; }
            RenameIds(from, to);
            why = Change(SyncDoc.Op("rename", "pl", p.Key, "name", newName));
            if (why != null) return why;
            folderNames[p.Key] = newName;
            SaveLibrary();
            Log("renamed playlist " + p.Name + " to " + newName);
            return null;
        }

        // Directory.Move, also when only the capitals change (Windows needs a step in between for that).
        static void MoveFolder(string from, string to)
        {
            if (!Directory.Exists(from)) { Directory.CreateDirectory(to); return; }
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) { string tmp = from + ".renaming"; Directory.Move(from, tmp); Directory.Move(tmp, to); }
            else Directory.Move(from, to);
        }

        // Pictures of a renamed folder have new paths (their ids): the one on screen, a kept one and history follow.
        void RenameIds(string fromFolder, string toFolder)
        {
            Func<string, string> map = id =>
                id.StartsWith(fromFolder + "\\", StringComparison.OrdinalIgnoreCase) ? toFolder + id.Substring(fromFolder.Length) : id;
            for (int i = 0; i < rot.History.Count; i++) rot.History[i] = map(rot.History[i]);
            for (int i = 0; i < rot.Round.Count; i++) rot.Round[i] = map(rot.Round[i]);
            rot.LoadRound(rot.Round.ToList());
            WriteList("history.txt", rot.History);
            WriteList(RoundFile, rot.Round);
            if (settings.KeepId.Length > 0) { settings.KeepId = map(settings.KeepId); settings.Save(); }
            if (current != null && current.IsLocal)
            {
                string moved = map(current.LocalPath);
                if (moved != current.LocalPath) current = Wall.Local(moved, walls.FirstOrDefault(x => x.Id == Path.GetFileNameWithoutExtension(moved)));
            }
            folderCache.Clear();
        }

        // A playlist goes to the Recycle Bin, folder and all (it can be restored from there; a wallpaper still in the
        // cache or another playlist stays there). A folder of your own is only taken off the list, never touched.
        public bool DeleteSource(SourceInfo p)
        {
            if (p == null || p.Key == "toplist" || p.Key == "fav") return false;
            if (IsOn(p.Key)) ToggleSource(p.Key, false);
            if (p.Own)
            {
                bool shown = current != null && current.IsLocal && SameFolder(Path.GetDirectoryName(current.LocalPath), p.Folder);
                if (Directory.Exists(p.Folder) && !Native.Recycle(p.Folder)) { Log("couldn't move " + p.Name + " to the Recycle Bin"); return false; }
                if (shown && settings.KeepId == current.Id) ReleaseKeep("its playlist was deleted");
                Change(SyncDoc.Op("delete", "pl", p.Key));
                folderNames.Remove(p.Key); folderFiles.Remove(p.Key);
                SaveLibrary();
                Log("deleted playlist " + p.Name + " (in the Recycle Bin)");
            }
            else
            {
                folders.RemoveAll(f => string.Equals(f, p.Folder, StringComparison.OrdinalIgnoreCase));
                WriteList("folders.txt", folders);
                Log("stopped using the folder " + p.Folder);
            }
            Changed();
            return true;
        }

        // A folder of your own pictures, used where it is (nothing is copied), and turned on.
        public void AddFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            path = Path.GetFullPath(path).TrimEnd('\\');
            if (path.Length == 2 && path[1] == ':') path += "\\";   // a whole drive
            if (SameFolder(path, favouritesDir)) { ToggleSource("fav", true); return; }
            var pl = doc.Own.FirstOrDefault(p => SameFolder(Path.Combine(PlaylistsRoot, p.Name), path));
            if (pl != null) { ToggleSource(pl.Id, true); return; }
            if (!folders.Any(f => SameFolder(f, path))) { folders.Add(path); WriteList("folders.txt", folders); Log("added the folder " + path); }
            ToggleSource("folder:" + folders.First(f => SameFolder(f, path)), true);
        }

        // ---- the dialogs behind New / Rename / Delete / Add a folder (centred on the flyout when it's open)

        public void NewPlaylist(bool addCurrent, Form owner)
        {
            string name = FluentDialog.Ask(owner, "New playlist", addCurrent && current != null ? "Name it, and the wallpaper on screen goes in first." : "Name it. It shows up on every synced device.",
                "Create", "", n => CheckPlaylistName(n, null));
            if (name == null) return;
            string problem = CreatePlaylist(name, addCurrent);
            if (problem != null) Log("new playlist: " + problem);
        }

        public void RenamePlaylistDialog(SourceInfo p, Form owner)
        {
            if (p == null || !p.Own) return;
            string name = FluentDialog.Ask(owner, "Rename playlist", "A new name for \"" + p.Name + "\".", "Rename", p.Name, n => CheckPlaylistName(n, p.Key));
            if (name != null) { string problem = RenamePlaylist(p, name); if (problem != null) Log("rename: " + problem); }
        }

        public void DeleteSourceDialog(SourceInfo p, Form owner)
        {
            if (p == null || p.Key == "toplist" || p.Key == "fav") return;
            int n = p.Own ? Math.Max(p.Count, 0) : 0;
            bool ok = p.Own
                ? FluentDialog.Confirm(owner, "Delete \"" + p.Name + "\"?", "It goes from every synced device. Its folder and " + n + " picture" + (n == 1 ? "" : "s") + " go to the Recycle Bin; wallpapers it shares with other playlists stay there.", "Delete")
                : FluentDialog.Confirm(owner, "Stop using \"" + p.Name + "\"?", "The folder and its pictures stay where they are; it's only taken off this list.", "Stop using");
            if (ok) DeleteSource(p);
        }

        // A dialog is open (the flyout stays open behind it).
        public static int ModalOpen;

        public void AddFolderDialog(Form owner)
        {
            ModalOpen++;
            Form temp = null;
            try
            {
                if (owner == null || !owner.Visible) owner = temp = FluentDialog.TopOwner();   // from the tray: in front, not behind other windows
                using (var d = new FolderBrowserDialog { Description = "Pick a folder of pictures to shuffle. They're used where they are; nothing is copied.", ShowNewFolderButton = false })
                    if (d.ShowDialog(owner) == DialogResult.OK) AddFolder(d.SelectedPath);
            }
            finally { ModalOpen--; if (temp != null) temp.Dispose(); }
        }

        public void OpenPlaylists() { try { Directory.CreateDirectory(PlaylistsRoot); } catch (Exception) { } Open(PlaylistsRoot); }
        public void OpenOnWallhaven() { if (current != null) Open(current.Page); }
        public void OpenFavourites() { Directory.CreateDirectory(favouritesDir); Open(favouritesDir); }

        // ------------------------------------------------------------------------- folders in step with the playlists

        int reconciling;                      // one at a time; another request while one runs makes it go again
        bool reconcileAgain;

        // Brings the folders in line with the playlists (and the playlists with what was done in Explorer). The file
        // work runs on the side; what it finds comes back as changes.
        void Reconcile()
        {
            if (Interlocked.CompareExchange(ref reconciling, 1, 0) != 0) { reconcileAgain = true; return; }
            reconcileAgain = false;
            var snap = doc.Copy();
            var names = new Dictionary<string, string>(folderNames);
            var files = folderFiles.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
            var meta = walls.ToDictionary(w => w.Id, w => w);
            string root = PlaylistsRoot, fav = favouritesDir;
            ThreadPool.QueueUserWorkItem(delegate
            {
                var found = new List<Dictionary<string, object>>();
                try { ReconcileFolders(snap, names, files, meta, root, fav, found); }
                catch (Exception ex) { Log("playlists: " + ex.Message); }
                Post(delegate
                {
                    Interlocked.Exchange(ref reconciling, 0);
                    folderNames = names; folderFiles = files;
                    foreach (var op in found) { string why = doc.Apply(op); if (why == null) { if (Paired) outbox.Add(op); } else Log("from Explorer: " + why); }
                    SaveLibrary();
                    folderCache.Clear();
                    if (found.Count > 0) { Log("picked up " + found.Count + " change" + (found.Count == 1 ? "" : "s") + " made in Explorer"); KickSync(); }
                    Changed();
                    if (reconcileAgain) Reconcile();
                });
            });
        }

        // The work (no UI): see Reconcile. names/files are updated in place; changes found go into found.
        void ReconcileFolders(SyncDoc d, Dictionary<string, string> names, Dictionary<string, HashSet<string>> files,
            Dictionary<string, Wall> meta, string root, string favFolder, List<Dictionary<string, object>> found)
        {
            Directory.CreateDirectory(root);
            var own = d.Own.ToList();
            // 1. Folders: renamed elsewhere -> renamed here; deleted elsewhere -> to the Recycle Bin (only ones made
            //    for a playlist before); new in Explorer -> a new playlist (or, the first time, the one of that name).
            foreach (var p in own)
            {
                string old;
                if (names.TryGetValue(p.Id, out old) && old != p.Name && Directory.Exists(Path.Combine(root, old)) && !Directory.Exists(Path.Combine(root, p.Name)))
                {
                    MoveFolder(Path.Combine(root, old), Path.Combine(root, p.Name));
                    Log("renamed folder " + old + " to " + p.Name + " (renamed on another device)");
                }
                names[p.Id] = p.Name;
            }
            foreach (var id in names.Keys.ToList())
            {
                if (d.Find(id) != null) continue;
                string dir = Path.Combine(root, names[id]);
                if (Directory.Exists(dir) && !own.Any(p => string.Equals(p.Name, names[id], StringComparison.OrdinalIgnoreCase)))
                {
                    if (Native.Recycle(dir)) Log("playlist " + names[id] + " was deleted on another device: its folder is in the Recycle Bin");
                }
                names.Remove(id); files.Remove(id);
            }
            foreach (var dir in Directory.GetDirectories(root))
            {
                var info = new DirectoryInfo(dir);
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || info.Name.EndsWith(".renaming")) continue;
                if (names.Values.Any(n => string.Equals(n, info.Name, StringComparison.OrdinalIgnoreCase))) continue;
                var same = own.FirstOrDefault(p => string.Equals(p.Name, info.Name, StringComparison.OrdinalIgnoreCase));
                if (same != null) { names[same.Id] = info.Name; continue; }
                if (d.CheckName(info.Name, null) != null) continue;   // a name the other devices couldn't use: left alone
                string newId = SyncDoc.NewId();
                var op = SyncDoc.Op("create", "id", newId, "name", info.Name, "items", new object[0]);
                if (d.Apply(op) == null) { found.Add(op); names[newId] = info.Name; own.Add(d.Find(newId)); Log("new folder in Explorer: " + info.Name + " is a playlist now"); }
            }
            // 2. Pictures, playlist by playlist (Favourites too).
            var lists = new List<Tuple<SyncPlaylist, string>> { Tuple.Create(d.Find("fav"), favFolder) };
            foreach (var p in own) lists.Add(Tuple.Create(p, Path.Combine(root, p.Name)));
            var everywhere = lists.Select(t => t.Item2).ToList();
            foreach (var t in lists)
            {
                var p = t.Item1; string folder = t.Item2;
                if (p.Kind == "fav" && p.Items.Count == 0 && !Directory.Exists(folder)) continue;
                Directory.CreateDirectory(folder);
                HashSet<string> known;
                if (!files.TryGetValue(p.Id, out known)) known = new HashSet<string>();
                var present = new Dictionary<string, string>();
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    string id = Path.GetFileNameWithoutExtension(f);
                    if (Wall.IsImageFile(f) && SyncDoc.IsWallId(id) && new FileInfo(f).Length > 0) present[id] = f;
                }
                var ids = new HashSet<string>(p.Items.Select(x => x.Id));
                var now = new HashSet<string>();
                foreach (var it in p.Items.ToList())
                {
                    if (present.ContainsKey(it.Id)) { now.Add(it.Id); continue; }
                    if (known.Contains(it.Id))   // it was here and is gone: deleted in Explorer
                    {
                        found.Add(SyncDoc.Op("remove", "pl", p.Id, "id", it.Id));
                        continue;
                    }
                    try
                    {
                        string src = LocalCopyOf(it, everywhere);
                        if (src == null) src = Fetch(new Wall { Id = it.Id, Image = it.Image, Page = it.Page, Resolution = it.Res, Favorites = it.Fav, Extension = "." + it.Ext });
                        Native.LinkOrCopy(src, Path.Combine(folder, it.Id + "." + it.Ext));
                        now.Add(it.Id);
                        Log("added " + it.Id + " to " + p.Name + " (from another device)");
                    }
                    catch (Exception ex) { Log("couldn't get " + it.Id + " for " + p.Name + ": " + ex.Message); }
                }
                foreach (var kv in present)
                {
                    if (ids.Contains(kv.Key)) continue;
                    if (known.Contains(kv.Key))   // was synced, and taken out elsewhere
                    {
                        try { RemoveFile(kv.Value); Log("removed " + kv.Key + " from " + p.Name + " (taken out on another device)"); } catch (Exception) { now.Add(kv.Key); }
                        continue;
                    }
                    Wall m;
                    meta.TryGetValue(kv.Key, out m);
                    var item = SyncDoc.MakeItem(kv.Key, Path.GetExtension(kv.Value), m != null ? m.Resolution : "", m != null ? Math.Max(0, m.Favorites) : 0);
                    if (item == null) continue;
                    found.Add(SyncDoc.Op("add", "pl", p.Id, "item", SyncDoc.ItemDict(item)));
                    now.Add(kv.Key);
                }
                files[p.Id] = now;
            }
        }

        // The downloaded original, or the same picture in another playlist's folder, or null.
        string LocalCopyOf(SyncItem it, List<string> folders)
        {
            string cached = Path.Combine(cacheDir, it.Id + "." + it.Ext);
            if (File.Exists(cached) && new FileInfo(cached).Length > 0) return cached;
            foreach (var f in folders) { string p = FindInFolder(f, it.Id); if (p != null) return p; }
            return null;
        }

        // ------------------------------------------------------------------------- sync

        public bool Paired { get { return syncToken.Length > 0; } }
        public string SyncName { get { return syncName; } }
        public bool SyncReachable { get { return syncReachable; } }
        public string SyncError { get { return syncError; } }

        // Other devices (for "Show on ..."): id -> name, TVs first.
        public List<KeyValuePair<string, string>> OtherDevices()
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (var kv in doc.Devices.OrderBy(kv => Convert.ToString(kv.Value.ContainsKey("kind") ? kv.Value["kind"] : "") == "tv" ? 0 : 1))
            {
                if (kv.Key == syncId || !kv.Value.ContainsKey("settings")) continue;
                list.Add(new KeyValuePair<string, string>(kv.Key, Convert.ToString(kv.Value.ContainsKey("name") ? kv.Value["name"] : "Device")));
            }
            return list;
        }

        System.Threading.Timer syncTimer;
        int syncRunning;

        void StartSync()
        {
            syncTimer = new System.Threading.Timer(delegate { SyncOnce(); }, null, 3000, Timeout.Infinite);
        }

        void KickSync() { if (syncTimer != null) syncTimer.Change(300, Timeout.Infinite); }

        void MarkSettingsDirty() { if (!Paired) return; settingsDirty = true; SaveLibrary(); KickSync(); }

        DeviceSettings LocalDeviceSettings()
        {
            return new DeviceSettings
            {
                Sources = settings.Sources.Where(k => !k.StartsWith("folder:")).ToList(),
                Filters = settings.Filters.Copy(),
                Every = Math.Max(1, (int)Math.Round(settings.IntervalSeconds / 60.0)),
                Paused = settings.Paused,
                Keep = settings.KeepId
            };
        }

        // One check-in, on a worker thread: sign in with SyncKey if set, send changes, send what's on screen, get
        // the playlists (when they changed) and commands.
        void SyncOnce()
        {
            if (Interlocked.CompareExchange(ref syncRunning, 1, 0) != 0) return;
            int wait = 20000;
            try
            {
                string server = settings.Server;
                if (!Paired && settings.SyncKey.Length > 0 && server.Length > 0) Register(server, settings.SyncKey);
                if (!Paired || server.Length == 0) { wait = 60000; return; }
                // what to send, read on the UI thread
                List<Dictionary<string, object>> ops = null; Dictionary<string, object> st = null, patch = null; int v = 0, cs = 0; string thumbFor = null;
                Invoke(new Action(delegate
                {
                    ops = outbox.ToList();
                    if (settingsDirty) patch = LocalDeviceSettings().ToDict();
                    st = StatusForRemote(out thumbFor);
                    v = syncV; cs = cmdSeq;
                }));
                if (thumbFor != null) { string t = Thumb(thumbFor); if (t != null) ((Dictionary<string, object>)st["cur"])["thumb"] = t; }
                if (patch != null) ops.Add(SyncDoc.Op("settings", "device", syncId, "patch", patch));
                Dictionary<string, object> sent = null;
                if (ops.Count > 0)
                {
                    sent = SyncClient.Call(server, "op", new Dictionary<string, object> { { "ops", ops.Cast<object>().ToArray() } }, syncToken);
                    object errs;
                    if (sent.TryGetValue("errors", out errs) && errs is object[] && ((object[])errs).Length > 0) Log("sync: " + Convert.ToString(((Dictionary<string, object>)((object[])errs)[0])["error"]));
                }
                string hash = Json.Write(st).GetHashCode().ToString("x");
                var body = new Dictionary<string, object> { { "v", sent != null ? -1 : v }, { "cs", cs } };
                if (hash != stHash) body["st"] = st;
                var r = SyncClient.Call(server, "poll", body, syncToken);
                int sentOps = ops.Count - (patch != null ? 1 : 0);
                Post(delegate { AfterPoll(r, sentOps, patch != null, hash); });
                object live;
                syncLive = r.TryGetValue("live", out live) && live is bool && (bool)live;
                syncReachable = true; syncError = "";
                wait = syncLive ? 3000 : locked ? 60000 : 20000;
            }
            catch (SyncError ex)
            {
                bool same = ex.Message == syncError;   // a lasting problem is logged once, not every minute
                syncReachable = ex.Status != 0; syncError = ex.Message;
                if (ex.Status == 401) Post(delegate { SignedOut("signed out on another device"); });
                else if (!same) Log("sync: " + ex.Message);
                wait = 60000;
            }
            catch (Exception ex) { if (ex.Message != syncError) Log("sync: " + ex.Message); syncError = ex.Message; wait = 60000; }
            finally
            {
                Interlocked.Exchange(ref syncRunning, 0);
                try { syncTimer.Change(wait, Timeout.Infinite); } catch (ObjectDisposedException) { }
            }
        }

        // The answer of a check-in, on the UI thread.
        void AfterPoll(Dictionary<string, object> r, int sentOps, bool sentSettings, string hash)
        {
            outbox.RemoveRange(0, Math.Min(sentOps, outbox.Count));
            if (sentSettings) settingsDirty = false;
            stHash = hash;
            object v, docObj, cs, cmds;
            if (r.TryGetValue("cs", out cs)) try { cmdSeq = Convert.ToInt32(cs); } catch (Exception) { }
            if (r.TryGetValue("doc", out docObj) && docObj is Dictionary<string, object>)
            {
                var fresh = SyncDoc.FromDict((Dictionary<string, object>)docObj);
                foreach (var op in outbox) fresh.Apply(op);   // changes made meanwhile stay
                doc = fresh;
                if (r.TryGetValue("v", out v)) try { syncV = Convert.ToInt32(v); } catch (Exception) { }
                ApplyRemoteSettings();
                folderCache.Clear();
                Reconcile();
                // the one on screen was hidden elsewhere: move on
                if (current != null && current.WallhavenId != null && doc.Hidden.Contains(current.WallhavenId) && !busy) { prepared = null; Next("hidden on another device"); }
            }
            SaveLibrary();
            if (r.TryGetValue("cmds", out cmds) && cmds is object[])
                foreach (var c in (object[])cmds) Command(c as Dictionary<string, object>);
            Changed();
        }

        // This PC's settings as changed on another device (unless a change made here is still on its way).
        void ApplyRemoteSettings()
        {
            if (settingsDirty) return;
            Dictionary<string, object> me;
            object s;
            if (!doc.Devices.TryGetValue(syncId, out me)) return;
            object name;
            if (me.TryGetValue("name", out name)) syncName = Convert.ToString(name);
            if (!me.TryGetValue("settings", out s)) return;
            var remote = DeviceSettings.FromDict(s as Dictionary<string, object>);
            if (remote == null || remote.Signature == LocalDeviceSettings().Signature) return;
            bool listChanged = remote.Filters.ListQuery() != settings.Filters.ListQuery();
            settings.Filters = remote.Filters;
            if (remote.Every * 60 != settings.IntervalSeconds) { settings.IntervalSeconds = remote.Every * 60; nextChange = DateTime.Now.AddSeconds(Math.Min(settings.IntervalSeconds, Math.Max(0, (nextChange - DateTime.Now).TotalSeconds))); }
            if (remote.Paused != settings.Paused) { settings.Paused = remote.Paused; if (!settings.Paused) nextChange = DateTime.Now.AddSeconds(settings.IntervalSeconds); }
            if (remote.Keep != settings.KeepId)
            {
                if (remote.Keep.Length == 0) ReleaseKeep("taken off on another device");
                else if (current != null && (remote.Keep == current.Id || remote.Keep == current.WallhavenId)) { settings.KeepId = current.Id; Log("keeping " + Describe(current) + " (from another device)"); }
            }
            settings.Save();
            Log("settings changed on another device");
            var next = remote.Sources.Where(k => k == "toplist" || doc.Find(k) != null).ToList();
            next.AddRange(settings.Sources.Where(k => k.StartsWith("folder:")));
            if (listChanged) { nextListRefresh = DateTime.MinValue; prepared = null; }
            SetSources(next, false);
            if (window != null && !window.IsDisposed) window.LoadValues();
        }

        void Command(Dictionary<string, object> c)
        {
            if (c == null) return;
            object type;
            c.TryGetValue("type", out type);
            switch (Convert.ToString(type))
            {
                case "next": Next("remote"); break;
                case "prev": Previous(); break;
                case "refresh": nextListRefresh = DateTime.MinValue; Log("a fresh list (asked from the remote)"); break;
                case "local":
                {
                    object key, on;
                    c.TryGetValue("key", out key); c.TryGetValue("on", out on);
                    string k = Convert.ToString(key);
                    if (k.StartsWith("folder:") && folders.Contains(k.Substring(7))) ToggleSource(k, on is bool && (bool)on);
                    break;
                }
                case "show":
                {
                    object item;
                    c.TryGetValue("item", out item);
                    var d = item as Dictionary<string, object>;
                    if (d == null) break;
                    var it = SyncDoc.MakeItem(Convert.ToString(d["id"]), d.ContainsKey("ext") ? Convert.ToString(d["ext"]) : "jpg", d.ContainsKey("res") ? Convert.ToString(d["res"]) : "", 0);
                    if (it == null) break;
                    var w = walls.FirstOrDefault(x => x.Id == it.Id) ?? new Wall { Id = it.Id, Image = it.Image, Page = it.Page, Resolution = it.Res, Favorites = -1, Extension = "." + it.Ext };
                    if (busy) break;
                    ReleaseKeep("another wallpaper was sent");
                    Log("showing " + it.Id + " (sent from another device)");
                    ShowWall(w, -1);
                    break;
                }
            }
        }

        // What the remote shows for this PC. thumbFor: a picture of your own whose thumbnail should go along.
        Dictionary<string, object> StatusForRemote(out string thumbFor)
        {
            thumbFor = null;
            var st = new Dictionary<string, object> { { "app", "pc 2.2" } };
            if (current != null)
            {
                var item = ItemOf(current);
                var cur = item != null
                    ? new Dictionary<string, object> { { "key", current.Id }, { "id", item.Id }, { "ext", item.Ext }, { "res", item.Res.Length > 0 ? item.Res : current.Resolution }, { "fav", Math.Max(0, current.Favorites) } }
                    : new Dictionary<string, object> { { "key", current.Id }, { "local", true }, { "name", Path.GetFileNameWithoutExtension(current.LocalPath ?? current.Id) } };
                if (item == null) thumbFor = current.LocalPath;
                st["cur"] = cur;
            }
            var pool = Pool();
            var ids = new HashSet<string>(pool.Select(w => w.Id));
            st["shown"] = current == null ? 0 : rot.Counter(current.Id, ids);
            st["total"] = pool.Count;
            st["next"] = settings.Paused || IsKept || busy ? 0 : (long)(nextChange.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds;
            st["paused"] = settings.Paused;
            st["kept"] = IsKept;
            var recent = new List<object>();
            for (int i = rot.History.Count - 2; i >= 0 && recent.Count < 20; i--)
            {
                string id = rot.History[i];
                if (id.IndexOf('\\') >= 0) { string n = Path.GetFileNameWithoutExtension(id); if (SyncDoc.IsWallId(n)) id = n; else { recent.Add(new Dictionary<string, object> { { "local", true }, { "name", n } }); continue; } }
                var w = walls.FirstOrDefault(x => x.Id == id) ?? ItemWall(id);
                recent.Add(new Dictionary<string, object> { { "id", id }, { "ext", w != null ? w.Extension.TrimStart('.') : "jpg" }, { "res", w != null ? w.Resolution : "" }, { "fav", w != null ? Math.Max(0, w.Favorites) : 0 } });
            }
            st["recent"] = recent.ToArray();
            st["local"] = folders.Select(f => (object)new Dictionary<string, object> { { "key", "folder:" + f }, { "name", FolderLabel(f) }, { "count", FolderPool(f).Count }, { "on", IsOn("folder:" + f) } }).ToArray();
            int maxw = settings.Filters.MaxWidth;
            st["counts"] = new Dictionary<string, object> { { "toplist", walls.Count(w => maxw == 0 || WidthOf(w) <= maxw) } };
            st["list"] = listFor;
            if (!HasSource || settings.Sources.Count == 0) st["note"] = "Nothing to shuffle: turn something on in Shuffle from";
            return st;
        }

        readonly Dictionary<string, string> thumbs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // A small JPEG of a picture, as a data: address (for the remote, which can't see files on this PC).
        string Thumb(string path)
        {
            string t;
            if (string.IsNullOrEmpty(path)) return null;
            lock (thumbs) if (thumbs.TryGetValue(path, out t)) return t;
            try
            {
                using (var src = Image.FromFile(path))
                {
                    int w = 320, h = Math.Max(1, src.Height * w / Math.Max(1, src.Width));
                    using (var bmp = new Bitmap(w, h))
                    using (var g = Graphics.FromImage(bmp))
                    using (var ms = new MemoryStream())
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, 0, 0, w, h);
                        var enc = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
                        var p = new EncoderParameters(1);
                        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
                        bmp.Save(ms, enc, p);
                        t = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch (Exception) { t = null; }
            lock (thumbs) { if (thumbs.Count > 20) thumbs.Clear(); thumbs[path] = t; }
            return t;
        }

        // ---- signing in

        void Register(string server, string key)
        {
            var r = SyncClient.Call(server, "register", new Dictionary<string, object> { { "key", key }, { "kind", "pc" }, { "name", PcName() }, { "settings", LocalDeviceSettingsSafe() } }, null);
            Post(delegate { SignedIn(r); settings.SyncKey = ""; settings.Save(); });
        }

        Dictionary<string, object> LocalDeviceSettingsSafe() { Dictionary<string, object> d = null; Invoke(new Action(delegate { d = LocalDeviceSettings().ToDict(); })); return d; }

        static string PcName()
        {
            // Windows names a PC like DESKTOP-4F2K9QX or STUDIO-LAPTOP: the made-up ones become "PC" / "Laptop", others are tidied
            string n = Environment.MachineName ?? "PC";
            if (n.StartsWith("DESKTOP-")) return "PC";
            if (n.StartsWith("LAPTOP-")) return "Laptop";
            var words = n.Split(new[] { (char)45, (char)95, (char)32 }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Substring(0, 1).ToUpperInvariant() + w.Substring(1).ToLowerInvariant());
            return string.Join(" ", words);
        }

        // Signed in (with the key, or a pairing code): the playlists made here go up to the service, merged with the
        // ones there (same name = the same playlist).
        void SignedIn(Dictionary<string, object> r)
        {
            object token, id, name, d;
            r.TryGetValue("token", out token); r.TryGetValue("id", out id); r.TryGetValue("name", out name); r.TryGetValue("doc", out d);
            syncToken = Convert.ToString(token); syncId = Convert.ToString(id); syncName = Convert.ToString(name);
            var server = d is Dictionary<string, object> ? SyncDoc.FromDict((Dictionary<string, object>)d) : new SyncDoc();
            var mine = doc;
            outbox.Clear();
            foreach (var p in mine.Playlists)
            {
                var there = p.Kind != "" ? server.Find(p.Id) : server.Own.FirstOrDefault(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (there == null)
                {
                    outbox.Add(SyncDoc.Op("create", "id", p.Id, "name", p.Name, "items", p.Items.Select(i => (object)SyncDoc.ItemDict(i)).ToArray()));
                    continue;
                }
                if (there.Id != p.Id)
                {
                    // the same playlist under another id: its folder and records follow the service's id
                    string n; HashSet<string> f;
                    if (folderNames.TryGetValue(p.Id, out n)) { folderNames.Remove(p.Id); folderNames[there.Id] = n; }
                    if (folderFiles.TryGetValue(p.Id, out f)) { folderFiles.Remove(p.Id); folderFiles[there.Id] = f; }
                    settings.Sources = settings.Sources.Select(k => k == p.Id ? there.Id : k).ToList();
                }
                foreach (var it in p.Items) if (!there.Has(it.Id)) outbox.Add(SyncDoc.Op("add", "pl", there.Id, "item", SyncDoc.ItemDict(it)));
            }
            foreach (var op in outbox) server.Apply(op);
            doc = server;
            syncV = -1; cmdSeq = 0; stHash = ""; settingsDirty = true;
            settings.Save();
            SaveLibrary();
            Log("synced as " + syncName + (outbox.Count > 0 ? " (" + outbox.Count + " changes from this PC to send)" : ""));
            KickSync();
            Changed();
        }

        void SignedOut(string why)
        {
            if (!Paired) return;
            syncToken = ""; syncId = ""; syncV = -1; outbox.Clear(); settingsDirty = false;
            SaveLibrary();
            Log("sync: " + why + "; carrying on with this PC's own copy of the playlists");
            Changed();
        }

        // ---- the Remote menu's actions

        public void ConnectWithKey(Form owner)
        {
            if (!NeedServer(owner)) return;
            string key = FluentDialog.Ask(owner, "Connect this PC", "Type the sync key (SYNC_KEY in your Vercel project). Then the playlists sync, and the remote can change this PC's settings.", "Connect", "", k => k.Trim().Length < 8 ? "That's too short for a sync key" : null);
            if (key == null) return;
            string server = settings.Server;
            RunSync(owner, "Connecting…", delegate { Register(server, key.Trim()); }, "Connected");
        }

        public void PairWithCode(Form owner)
        {
            if (!NeedServer(owner)) return;
            string server = settings.Server;
            Dictionary<string, object> settingsNow = LocalDeviceSettings().ToDict();
            var r = FluentDialog.ShowPairing(owner, "Pair this PC",
                "On your phone, open the remote and choose Devices > Pair a device, then type this code.",
                delegate
                {
                    var p = SyncClient.Call(server, "pair", new Dictionary<string, object> { { "kind", "pc" }, { "name", PcName() }, { "settings", settingsNow } }, null);
                    return Tuple.Create(Convert.ToString(p["code"]), Convert.ToString(p["claim"]), Convert.ToInt64(p["expires"]));
                },
                claim =>
                {
                    var c = SyncClient.Call(server, "claim", new Dictionary<string, object> { { "claim", claim } }, null);
                    return c.ContainsKey("token") ? c : null;
                });
            if (r != null) { SignedIn(r); Log("paired"); }
        }

        public void PairDevice(Form owner)
        {
            if (!Paired) return;
            string code = FluentDialog.Ask(owner, "Pair a device", "Type the code the TV or the phone shows (TV: Wallhaven Shuffle > Pair; phone: open the remote).", "Pair", "",
                c => System.Text.RegularExpressions.Regex.Replace(c, @"\D", "").Length == 6 ? null : "The code has 6 digits");
            if (code == null) return;
            string server = settings.Server, token = syncToken, digits = System.Text.RegularExpressions.Regex.Replace(code, @"\D", "");
            Dictionary<string, object> r = null;
            RunSync(owner, "Pairing…", delegate { r = SyncClient.Call(server, "approve", new Dictionary<string, object> { { "code", digits } }, token); }, null);
            if (r != null) { Log("paired " + Convert.ToString(r["name"])); KickSync(); FluentDialog.Confirm(owner, "Paired " + Convert.ToString(r["name"]), "It syncs from now on. Change what it shows from the remote.", "OK", false); }
        }

        public void OpenRemote()
        {
            string server = settings.Server, token = syncToken;
            if (server.Length == 0) return;
            if (!Paired) { Open(server + "/"); return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var r = SyncClient.Call(server, "link", null, token);
                    Open(server + "/#link=" + Convert.ToString(r["code"]));   // a one-time code, used up within two minutes
                }
                catch (Exception ex) { Log("remote: " + ex.Message); Open(server + "/"); }
            });
        }

        public void SignOutDialog(Form owner)
        {
            if (!Paired) return;
            if (!FluentDialog.Confirm(owner, "Sign out of sync?", "This PC keeps its playlists and settings, and stops syncing. Pair again any time.", "Sign out")) return;
            string server = settings.Server, token = syncToken;
            ThreadPool.QueueUserWorkItem(delegate { try { SyncClient.Call(server, "forget", null, token); } catch (Exception) { } });
            SignedOut("signed out here");
        }

        public void ShowOn(string deviceId, string name)
        {
            var item = ItemOf(current);
            if (item == null || !Paired) return;
            string server = settings.Server, token = syncToken;
            var body = new Dictionary<string, object> { { "device", deviceId }, { "cmd", new Dictionary<string, object> { { "type", "show" }, { "item", SyncDoc.ItemDict(item) } } } };
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { SyncClient.Call(server, "cmd", body, token); Log("sent " + item.Id + " to " + name); }
                catch (Exception ex) { Log("show on " + name + ": " + ex.Message); }
            });
        }

        bool NeedServer(Form owner)
        {
            if (settings.Server.Length > 0) return true;
            string s = FluentDialog.Ask(owner, "The service address", "Your wallhaven-shuffle deployment, for example https://your-project.vercel.app", "Save", "",
                v => Uri.IsWellFormedUriString(v.Trim().StartsWith("http") ? v.Trim() : "https://" + v.Trim(), UriKind.Absolute) ? null : "That isn't a web address");
            if (s == null) return false;
            s = s.Trim().TrimEnd('/');
            settings.Server = s.StartsWith("http") ? s : "https://" + s;
            settings.Save();
            nextListRefresh = DateTime.MinValue;
            return true;
        }

        // Runs a request with the flyout waiting (a few seconds at most); shows what went wrong, if anything.
        void RunSync(Form owner, string what, Action work, string done)
        {
            string error = null;
            Cursor.Current = Cursors.WaitCursor;
            var t = new Thread(delegate () { try { work(); } catch (Exception ex) { error = ex.Message; } });
            t.Start();
            while (!t.Join(30)) Application.DoEvents();
            Cursor.Current = Cursors.Default;
            if (error != null) FluentDialog.Confirm(owner, "That didn't work", error, "OK", false);
            else if (done != null) Log(done);
        }
    }
}
