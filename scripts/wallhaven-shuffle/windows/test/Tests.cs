// Tests for Wallhaven Shuffle (build.ps1 -Test; compiled with WallhavenShuffle.cs, Sync.cs and Library.cs; run from a
// temporary folder). The TrayApp is constructed but its message loop never runs, so no timer fires; it's paused, and
// Favourites point at a test folder, so the real wallpaper and Pictures folder are never touched.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WallhavenShuffle
{
    static class Tests
    {
        static int fails, passes;
        static void Ok(bool c, string m) { Console.WriteLine((c ? "ok   " : "FAIL ") + m); if (c) passes++; else fails++; }
        const BindingFlags All = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        static object GetF(object o, string f) { return o.GetType().GetField(f, All).GetValue(o); }
        static void SetF(object o, string f, object v) { o.GetType().GetField(f, All).SetValue(o, v); }
        static object GetP(object o, string p) { return o.GetType().GetProperty(p, All).GetValue(o, null); }
        static object Call(object o, string m, params object[] a) { return o.GetType().GetMethod(m, All).Invoke(o, a); }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct BY_HANDLE_FILE_INFORMATION { public uint Attr; public long C, A, W; public uint Serial, SizeHi, SizeLo, Links, IdxHi, IdxLo; }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);
        static uint Links(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                BY_HANDLE_FILE_INFORMATION i;
                return GetFileInformationByHandle(fs.SafeFileHandle, out i) ? i.Links : 0;
            }
        }
        static void Img(string path, int bytes) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, Enumerable.Repeat((byte)7, bytes).ToArray()); }
        static Dictionary<string, object> D(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; }

        const string OldUrl = "https://example.invalid/api/list?range=1y&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=50&maxw=0";

        [STAThread]
        static int Main()
        {
            string dir = Settings.Dir, cache = Path.Combine(dir, "cache"), far = @"D:\whs-test-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Theme.Load();

            // ---------------------------------------------------------------- pure pieces
            var r = new Rotation(new[] { "a", "b" }, new[] { "a", "b" }, 1);
            r.LoadRound(new[] { "x", "x", "y" });
            Ok(r.Round.Count == 2 && r.InRound("x") && !r.InRound("a") && r.History.Count == 2 && r.Back == -1, "Rotation.LoadRound swaps the round (no duplicates), keeps history");
            var w1 = Wall.Local(@"C:\p\abc123.jpg", null);
            Ok(w1.Page == "https://wallhaven.cc/w/abc123" && w1.WallhavenId == "abc123" && w1.FileName == "abc123.jpg", "a saved picture keeps its Wallhaven page and id");
            var w2 = Wall.Local(@"C:\p\My Holiday Photo.PNG", null);
            Ok(w2.Page == null && w2.WallhavenId == null, "your own picture: no Wallhaven page or id");

            // filters: the same list query as the service, the TV and the remote
            var f = new Filters();
            Ok(f.ListQuery() == "range=1y&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=50&maxw=0", "default filters = the list the app always used: " + f.ListQuery());
            Ok(Filters.FromListUrl(OldUrl).ListQuery() == f.ListQuery(), "an old ListUrl becomes the same filters");
            var g = new Filters { Quality = "4k", Ratio = "", Topic = "  Night  CITY ", Fav = 7 }.Clean();
            Ok(g.ListQuery() == "range=1y&res=3840x2160,3840x2400&cats=110&purity=100&q=night%20city&fav=7&maxw=0", "exact 4K, any shape, a topic: " + g.ListQuery());
            var bad = new Filters { Quality = "9k", Ratio = "3x1", Range = "2y", Cats = "000", Topic = "<b>", Fav = -1, MaxWidth = 1234 }.Clean();
            Ok(bad.Quality == "4k+" && bad.Ratio == "16x9,16x10" && bad.Range == "1y" && bad.Cats == "110" && bad.Topic == "" && bad.Fav == 50 && bad.MaxWidth == 0, "odd filter values go back to valid ones");
            Ok(new Filters().Summary == "4K and up · Past year" && Filters.EveryLabel(120) == "2 hours" && Filters.FavLabel(1000) == "1,000+ favourites", "labels");

            // the document's rules (the same as the service's)
            var doc = new SyncDoc();
            Ok(doc.Find("fav") != null && doc.Find("hidden") != null && doc.Playlists[0].Id == "fav", "a new library has Favourites and Hidden");
            var item = D("id", "AbC123", "ext", "png", "res", "3840x2160", "fav", 5);
            Ok(doc.Apply(D("op", "add", "pl", "hidden", "item", item)) == null && doc.Hidden.Contains("abc123"), "hide (ids lower-cased)");
            Ok(doc.Apply(D("op", "create", "id", "p_test01", "name", " Cozy  evenings ", "items", new object[] { item })) == null && doc.Find("p_test01").Name == "Cozy evenings", "create (name tidied)");
            Ok(!doc.Hidden.Contains("abc123"), "putting a wallpaper in a playlist un-hides it");
            Ok(doc.Apply(D("op", "create", "id", "p_test01", "name", "Cozy evenings")) == null && doc.Own.Count() == 1, "creating again is harmless");
            Ok(doc.Apply(D("op", "create", "id", "p_test02", "name", "cozy EVENINGS")) == "There's already a playlist called cozy EVENINGS", "same name, any capitals, refused");
            Ok(doc.CheckName("a/b", null) == "A name can't contain \\ / : * ? \" < > |" && doc.CheckName("lpt1.txt", null) == "Windows reserves that name" && doc.CheckName("end.", null) != null, "Windows name rules, the service's words");
            Ok(doc.Apply(D("op", "rename", "pl", "fav", "name", "X")) != null && doc.Apply(D("op", "delete", "pl", "hidden")) != null, "Favourites and Hidden can't be renamed or deleted");
            Ok(doc.Apply(D("op", "remove", "pl", "p_test01", "id", "ABC123")) == null && doc.Find("p_test01").Items.Count == 0, "remove");
            Ok(doc.Apply(D("op", "add", "pl", "p_test01", "item", D("id", "../evil"))) != null, "only Wallhaven ids are taken");
            var copy = doc.Copy();
            Ok(copy.Find("p_test01").Name == "Cozy evenings" && copy.Playlists.Count == doc.Playlists.Count, "the document survives JSON");
            var ds = new DeviceSettings { Sources = new List<string> { "toplist", "p_test01" }, Every = 30, Paused = true, Keep = "abc123" };
            ds.Filters.Quality = "8k";
            var ds2 = DeviceSettings.FromDict(Json.Parse(Json.Write(ds.ToDict())));
            Ok(ds2.Signature == ds.Signature && ds2.Filters.Quality == "8k" && ds2.Every == 30, "device settings survive JSON");

            // ---------------------------------------------------------------- links, copies, Recycle Bin
            string a = Path.Combine(dir, "t", "a.jpg"), b = Path.Combine(dir, "t", "b.jpg"), c = Path.Combine(far, "c.jpg");
            Img(a, 1000);
            Ok(Native.LinkOrCopy(a, b) && Links(b) == 2, "same drive: hard link");
            Directory.CreateDirectory(far);
            Ok(!Native.LinkOrCopy(a, c) && File.Exists(c) && Links(c) == 1, "another drive: copied");
            Ok(Native.Recycle(b) && !File.Exists(b), "Recycle Bin: file gone from its folder");

            // ---------------------------------------------------------------- 2.1 settings and files move over
            string pl = Path.Combine(dir, "Playlists");
            Img(Path.Combine(pl, "Night", "one.jpg"), 300);
            File.WriteAllText(Settings.FilePath, "IntervalSeconds=120\r\nCacheDir=" + cache + "\r\nListUrl=" + OldUrl + "\r\nMinFavorites=50\r\nStartupDone=1\r\nOriginalWallpaper=none\r\nPaused=1\r\nSource=playlist:Night\r\n");
            File.WriteAllText(Path.Combine(dir, "blocked.txt"), "zz1111\r\nC:\\x\\own.jpg\r\n");
            Img(Path.Combine(cache, "abc123.jpg"), 2000);
            Img(Path.Combine(cache, "def456.jpg"), 2000);
            File.WriteAllText(Path.Combine(dir, "list.json"), "{\"items\":[" +
                "{\"id\":\"abc123\",\"path\":\"https://w.wallhaven.cc/full/ab/wallhaven-abc123.jpg\",\"url\":\"https://wallhaven.cc/w/abc123\",\"resolution\":\"3840x2160\",\"favorites\":321}," +
                "{\"id\":\"def456\",\"path\":\"https://w.wallhaven.cc/full/de/wallhaven-def456.jpg\",\"url\":\"https://wallhaven.cc/w/def456\",\"resolution\":\"5120x2880\",\"favorites\":99}],\"complete\":true}");
            var app = new TrayApp();
            string favs = Path.Combine(dir, "Favs");
            SetF(app, "favouritesDir", favs);   // never the real Pictures folder
            try
            {
                var s = app.settings;
                var d = (SyncDoc)GetF(app, "doc");
                Ok(s.Server == "https://example.invalid" && s.ListUrl == OldUrl, "ListUrl became the service and the filters (same address): " + s.ListUrl);
                var night = d.Own.FirstOrDefault(p => p.Name == "Night");
                Ok(night != null && s.Sources.SequenceEqual(new[] { night.Id }), "the 2.1 source playlist:Night became its playlist id");
                Ok(d.Hidden.Contains("zz1111") && File.ReadAllText(Path.Combine(dir, "blocked.txt")).Trim() == @"C:\x\own.jpg", "blocked.txt: the Wallhaven one moved to Hidden, your own picture stays local");
                s.Save();
                var saved = File.ReadAllText(Settings.FilePath);
                Ok(!saved.Contains("ListUrl=") && !saved.Contains("MinFavorites=") && saved.Contains("Server=https://example.invalid") && saved.Contains("Sources=" + night.Id), "settings.ini written the new way");

                // sources and the pool
                Ok(app.SourcesSummary == "Night" && app.HasSource, "one source: " + app.SourcesSummary);
                s.Sources = new List<string> { "toplist" };
                Ok((string)GetP(app, "RoundFile") == "seen.txt", "the toplist alone keeps seen.txt");
                Ok(((List<Wall>)Call(app, "Pool")).Count == 2, "the toplist: 2");
                s.Filters.MaxWidth = 3840;
                Ok(((List<Wall>)Call(app, "Pool")).Count == 1, "Largest size: up to 4K leaves out the 5K one");
                s.Filters.MaxWidth = 0;
                Img(Path.Combine(pl, "Night", "abc123.jpg"), 2000);
                s.Sources = new List<string> { "toplist", night.Id };
                ((System.Collections.IDictionary)GetF(app, "folderCache")).Clear();
                var pool = (List<Wall>)Call(app, "Pool");
                Ok(pool.Count == 3 && pool.First(x => x.WallhavenId == "abc123").IsLocal, "mixed: toplist + Night = 3, the downloaded copy in Night is used for abc123");
                string mix1 = (string)GetP(app, "RoundFile");
                s.Sources = new List<string> { night.Id, "toplist" };
                Ok(mix1.StartsWith("seen-") && (string)GetP(app, "RoundFile") == mix1, "a mix has its own round, whatever the order");
                Call(app, "Change", D("op", "add", "pl", "hidden", "item", D("id", "def456")));
                Ok(((List<Wall>)Call(app, "Pool")).All(x => x.WallhavenId != "def456"), "a hidden one is left out everywhere");
                Ok(app.Status().StartsWith("Paused"), "status while paused: " + app.Status());

                // playlists: made, linked, renamed, deleted
                Ok(app.CreatePlaylist("  Alpha ", false) == null && Directory.Exists(Path.Combine(pl, "Alpha")), "playlist made, with its folder");
                Ok(app.CreatePlaylist("alpha", false) != null, "same name refused");
                var alpha = app.Sources().First(x => x.Name == "Alpha");
                SetF(app, "current", ((List<Wall>)GetF(app, "walls")).First(x => x.Id == "abc123"));
                app.TogglePlaylist(alpha);
                string inAlpha = Path.Combine(pl, "Alpha", "abc123.jpg");
                Ok(File.Exists(inAlpha) && Links(inAlpha) >= 2 && d.Find(alpha.Key).Has("abc123") && app.InPlaylist(alpha, app.Current), "added: a link in its folder and in the playlist");
                app.Favourite();
                Ok(File.Exists(Path.Combine(favs, "abc123.jpg")) && app.IsFavourite && d.Find("fav").Has("abc123"), "favourite: linked into the (test) Favourites folder and the playlist");
                app.Favourite();
                Ok(!File.Exists(Path.Combine(favs, "abc123.jpg")) && !app.IsFavourite, "unfavourite: to the Recycle Bin");
                Ok(app.RenamePlaylist(alpha, "ALPHA") == null && Directory.GetDirectories(pl).Any(x => Path.GetFileName(x) == "ALPHA") && d.Find(alpha.Key).Name == "ALPHA", "rename with only capitals changed");
                alpha = app.Sources().First(x => x.Key == alpha.Key);
                app.TogglePlaylist(alpha);
                Ok(!File.Exists(Path.Combine(pl, "ALPHA", "abc123.jpg")) && !d.Find(alpha.Key).Has("abc123") && File.Exists(Path.Combine(cache, "abc123.jpg")), "taken out: link to the Recycle Bin, the cache keeps its file");
                var own = Wall.Local(Path.Combine(pl, "Night", "one.jpg"), null);
                SetF(app, "current", own);
                app.TogglePlaylist(alpha);
                Ok(File.Exists(Path.Combine(pl, "ALPHA", "one.jpg")) && d.Find(alpha.Key).Items.Count == 0, "your own picture goes into the folder only (it isn't synced)");

                // keep (local, not paired: nothing to send)
                app.ToggleKeep();
                Ok(app.IsKept && Settings.Load().KeepId == own.Id && !(bool)GetF(app, "settingsDirty"), "keep works without sync");
                app.ToggleKeep();

                // hiding while a change runs: queued, never shown here
                SetF(app, "current", ((List<Wall>)GetF(app, "walls")).First(x => x.Id == "abc123"));
                SetF(app, "busy", true);
                app.Block();
                Ok(d.Hidden.Contains("abc123") && app.HiddenCount == 4, "Never show this one: into Hidden (" + app.HiddenCount + " hidden)");
                app.UnhideAll();
                Ok(app.HiddenCount == 0 && d.Hidden.Count == 0, "show them all again");
                SetF(app, "busy", false);

                // ---------------------------------------------------------------- folders in step with the playlists
                var snap = d.Copy();
                var names = new Dictionary<string, string>((Dictionary<string, string>)GetF(app, "folderNames"));
                var files = ((Dictionary<string, HashSet<string>>)GetF(app, "folderFiles")).ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
                var meta = ((List<Wall>)GetF(app, "walls")).ToDictionary(w => w.Id, w => w);
                var found = new List<Dictionary<string, object>>();
                snap.Apply(D("op", "add", "pl", alpha.Key, "item", D("id", "def456", "res", "5120x2880")));   // (a) from another device
                Img(Path.Combine(pl, "Night", "ccc999.png"), 400);                                              // (b) dropped in Explorer
                Img(Path.Combine(pl, "From Explorer", "ddd111.jpg"), 400);                                      // (c) a new folder
                Call(app, "ReconcileFolders", snap, names, files, meta, pl, favs, found);
                Ok(File.Exists(Path.Combine(pl, "ALPHA", "def456.jpg")) && Links(Path.Combine(pl, "ALPHA", "def456.jpg")) >= 2, "(a) added on another device: linked from the cache");
                Ok(found.Any(o => (string)o["op"] == "add" && (string)o["pl"] == night.Id && (string)((Dictionary<string, object>)o["item"])["id"] == "ccc999" && (string)((Dictionary<string, object>)o["item"])["ext"] == "png"), "(b) dropped into a folder: joins the playlist");
                var created = found.FirstOrDefault(o => (string)o["op"] == "create");
                Ok(created != null && (string)created["name"] == "From Explorer", "(c) a new folder becomes a playlist");
                Ok(created != null && found.Any(o => (string)o["op"] == "add" && (string)o["pl"] == (string)created["id"]), "(c) ... with its pictures");
                Ok(File.Exists(Path.Combine(pl, "ALPHA", "one.jpg")), "pictures that aren't Wallhaven ones are left alone");
                foreach (var op in found) snap.Apply(op);
                found.Clear();
                File.Delete(Path.Combine(pl, "Night", "ccc999.png"));                         // (d) deleted in Explorer
                snap.Apply(D("op", "remove", "pl", alpha.Key, "id", "def456"));               // (e) taken out elsewhere
                var fe = snap.Own.First(p => p.Name == "From Explorer");
                snap.Apply(D("op", "delete", "pl", fe.Id));                                   // (f) deleted elsewhere
                snap.Apply(D("op", "rename", "pl", night.Id, "name", "Night owls"));          // (g) renamed elsewhere
                Call(app, "ReconcileFolders", snap, names, files, meta, pl, favs, found);
                Ok(found.Any(o => (string)o["op"] == "remove" && (string)o["id"] == "ccc999"), "(d) deleted in Explorer: taken out of the playlist");
                Ok(!File.Exists(Path.Combine(pl, "ALPHA", "def456.jpg")), "(e) taken out on another device: its file goes to the Recycle Bin");
                Ok(!Directory.Exists(Path.Combine(pl, "From Explorer")), "(f) deleted on another device: its folder goes to the Recycle Bin");
                Ok(Directory.Exists(Path.Combine(pl, "Night owls")) && !Directory.Exists(Path.Combine(pl, "Night")), "(g) renamed on another device: the folder follows");
                Ok(found.Count == 1, "nothing else changed (" + found.Count + ")");
                found.Clear();
                Call(app, "ReconcileFolders", snap, names, files, meta, pl, favs, found);
                Ok(found.Count == 0, "a third pass finds nothing to do");

                // ---------------------------------------------------------------- signing in: merged with the service's playlists
                SetF(app, "folderNames", names); SetF(app, "folderFiles", files);
                d.Apply(D("op", "rename", "pl", night.Id, "name", "Night owls"));
                var server = new SyncDoc();
                server.Apply(D("op", "create", "id", "p_server1", "name", "alpha", "items", new object[] { D("id", "eee222") }));
                server.Apply(D("op", "add", "pl", "fav", "item", D("id", "fff333")));
                d.Apply(D("op", "add", "pl", "fav", "item", D("id", "abc123")));
                d.Apply(D("op", "add", "pl", alpha.Key, "item", D("id", "abc123")));
                s.Sources = new List<string> { "toplist", alpha.Key };
                Call(app, "SignedIn", D("token", "tok12345ab.secret", "id", "pcid000001", "name", "Laptop", "doc", server.ToDict()));
                var merged = (SyncDoc)GetF(app, "doc");
                var outbox = (List<Dictionary<string, object>>)GetF(app, "outbox");
                Ok(app.Paired && app.SyncName == "Laptop", "signed in as Laptop");
                Ok(merged.Find("p_server1").Has("abc123") && merged.Find("p_server1").Has("eee222") && merged.Own.Count(p => p.Name.Equals("alpha", StringComparison.OrdinalIgnoreCase)) == 1, "same name = the same playlist: merged, not doubled");
                Ok(s.Sources.Contains("p_server1") && ((Dictionary<string, string>)GetF(app, "folderNames")).ContainsKey("p_server1"), "the source and its folder follow the service's id");
                Ok(merged.Find("fav").Has("abc123") && merged.Find("fav").Has("fff333"), "favourites from both");
                Ok(outbox.Any(o => (string)o["op"] == "add" && (string)o["pl"] == "p_server1") && outbox.Any(o => (string)o["op"] == "create" && (string)o["name"] == "Night owls"), "what this PC had goes up: " + outbox.Count + " changes");

                // ---------------------------------------------------------------- settings changed on another device
                SetF(app, "settingsDirty", false);
                var remoteDoc = merged.Copy();
                var rs = new DeviceSettings { Sources = new List<string> { "fav", "p_gone00" }, Every = 30, Paused = true };
                rs.Filters.Quality = "8k"; rs.Filters.Topic = "space";
                remoteDoc.Devices["pcid000001"] = D("name", "Work laptop", "kind", "pc", "settings", rs.ToDict());
                Call(app, "AfterPoll", D("v", 7, "cs", 3, "doc", Json.Parse(Json.Write(remoteDoc.ToDict()))), 0, false, "h");
                Ok(s.Filters.Quality == "8k" && s.Filters.Topic == "space" && s.IntervalSeconds == 1800, "filters and interval from the remote");
                Ok(s.Sources.SequenceEqual(new[] { "fav" }), "sources from the remote (an unknown playlist dropped): " + string.Join(",", s.Sources));
                Ok(app.SyncName == "Work laptop" && (int)GetF(app, "syncV") == 7 && (int)GetF(app, "cmdSeq") == 3, "the name, version and command number taken");
                SetF(app, "nextListRefresh", DateTime.Now.AddHours(1));
                Call(app, "AfterPoll", D("cs", 4, "cmds", new object[] { D("type", "refresh", "seq", 4) }), 0, false, "h");
                Ok((DateTime)GetF(app, "nextListRefresh") == DateTime.MinValue, "the refresh command asks for a fresh list");
                Call(app, "SignedOut", "test");
                Ok(!app.Paired && ((List<Dictionary<string, object>>)GetF(app, "outbox")).Count == 0 && merged.Own.Any(), "signed out: keeps its playlists");

                // what the remote sees
                using (var bmp = new Bitmap(64, 36)) { using (var gr = Graphics.FromImage(bmp)) gr.Clear(Color.Teal); bmp.Save(Path.Combine(dir, "photo.jpg"), System.Drawing.Imaging.ImageFormat.Jpeg); }
                SetF(app, "current", Wall.Local(Path.Combine(dir, "photo.jpg"), null));
                var args = new object[] { null };
                var st = (Dictionary<string, object>)app.GetType().GetMethod("StatusForRemote", All).Invoke(app, args);
                Ok(((Dictionary<string, object>)st["cur"]).ContainsKey("local") && (string)args[0] == Path.Combine(dir, "photo.jpg"), "your own picture: shown as local, with a thumbnail to send");
                string t = (string)Call(app, "Thumb", (string)args[0]);
                Ok(t != null && t.StartsWith("data:image/jpeg;base64,") && t.Length < 40000, "thumbnail: a small JPEG (" + (t == null ? 0 : t.Length) + " chars)");
                Ok((bool)st["paused"] && st.ContainsKey("counts") && st.ContainsKey("recent"), "status has the pause, counts and recent ones");
                SetF(app, "current", ((List<Wall>)GetF(app, "walls")).First(x => x.Id == "abc123"));
                args = new object[] { null };
                st = (Dictionary<string, object>)app.GetType().GetMethod("StatusForRemote", All).Invoke(app, args);
                Ok((string)((Dictionary<string, object>)st["cur"])["id"] == "abc123" && args[0] == null, "a Wallhaven one: by its id, no thumbnail needed");

                // settings.ini round trip
                s.KeepId = @"C:\x\y.jpg"; s.Save();
                var back = Settings.Load();
                Ok(back.KeepId == @"C:\x\y.jpg" && back.Filters.Quality == "8k" && back.Filters.Topic == "space" && back.Server == "https://example.invalid" && back.Sources.SequenceEqual(new[] { "fav" }), "settings.ini keeps the new settings");
                s.KeepId = ""; s.Save();
            }
            finally
            {
                app.Close(); app.Dispose();
                try { Directory.Delete(far, true); } catch (Exception) { }
            }
            // A menu that's disposed as it closes (the window's + menu) still runs the item clicked: the action used to
            // be queued on the menu itself, after it closed, and was dropped with it (clicking a playlist did nothing).
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) };
                var hh = host.Handle;
                bool ran = false;
                var menu = new FluentMenu();
                var mi = menu.Add("Classic", Glyphs.Library, delegate { ran = true; });
                menu.Closed += delegate { host.BeginInvoke(new Action(menu.Dispose)); };   // as the window does
                menu.Show(new Point(-3000, -3000));
                for (int k = 0; k < 10; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                mi.PerformClick();
                for (int k = 0; k < 40; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                Ok(menu.IsDisposed, "the menu was disposed as it closed");
                Ok(ran, "a menu disposed as it closes still runs the item clicked (the window's + menu)");
                host.Dispose();
            }            // The same for a choice inside a submenu of such a menu (the window's Toplist > Quality, Change every, ...).
            {
                var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) };
                var hh = host.Handle;
                bool ran = false;
                ToolStripMenuItem choice = null;
                var menu = new FluentMenu();
                var parent = menu.AddSubmenu("Quality", null, m => { choice = m.Add("4K and up", null, delegate { ran = true; }); });
                menu.Closed += delegate { host.BeginInvoke(new Action(menu.Dispose)); };
                menu.Show(new Point(-3000, -3000));
                for (int k = 0; k < 10; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                parent.ShowDropDown();
                for (int k = 0; k < 10; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                if (choice != null) choice.PerformClick();
                for (int k = 0; k < 40; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                Ok(choice != null && ran, "a choice in a submenu of such a menu runs too (the window's Toplist, Change every)");
                host.Dispose();
            }            Console.WriteLine(fails == 0 ? "ALL PASSED (" + passes + ")" : fails + " FAILED, " + passes + " passed");
            return fails;
        }
    }
}
