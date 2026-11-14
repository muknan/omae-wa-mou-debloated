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
            // Never show while going back: out of the history (Previous never brings it back), and on from where it was
            {
                var rr = new Rotation(new string[0], new[] { "a", "b", "c", "d", "e" }, 1);
                rr.Back = 1;   // on b
                int follow = (int)typeof(Rotation).GetMethod("Forget").Invoke(rr, new object[] { (Func<string, bool>)(x => x == "b") });
                Ok(follow == 1 && string.Join(",", rr.History) == "a,c,d,e" && rr.Back == -1, "hiding the one on screen while going back: gone from the history, Next carries on with the one after it (" + follow + ")");
                var r2 = new Rotation(new string[0], new[] { "a", "b", "c", "d", "e" }, 1);
                r2.Back = 3;   // on d, hiding b (elsewhere)
                int f2 = (int)typeof(Rotation).GetMethod("Forget").Invoke(r2, new object[] { (Func<string, bool>)(x => x == "b") });
                Ok(f2 == -1 && string.Join(",", r2.History) == "a,c,d,e" && r2.Back == 2, "hiding one further back: the place stays on the one on screen");
            }
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
            Ok(Filters.ParseFav("75") == 75 && Filters.ParseFav(" 1,500 ") == 1500 && Filters.ParseFav("0") == 0 && Filters.ParseFav("100000") == 100000
               && Filters.ParseFav("100001") == -1 && Filters.ParseFav("-5") == -1 && Filters.ParseFav("7.5") == -1 && Filters.ParseFav("") == -1 && Filters.ParseFav("abc") == -1, "a typed favourites minimum");
            // Popularity ranges: the same words as the remote and the TV
            Ok(Filters.FavLabel(0) == "Any" && Filters.FavLabel(250) == "250+ favourites" && Filters.FavLabel(0, 500) == "Up to 500 favourites" && Filters.FavLabel(250, 1000) == "250 to 1,000 favourites", "range labels");
            {
                int lo, hi;
                Ok(Filters.ParseRange("200", "1,000", out lo, out hi) == null && lo == 200 && hi == 1000, "200 to 1,000");
                Ok(Filters.ParseRange("", "300", out lo, out hi) == null && lo == 0 && hi == 300, "an empty At least is 0");
                Ok(Filters.ParseRange("75", " ", out lo, out hi) == null && lo == 75 && hi == 0, "an empty At most is no limit");
                Ok(Filters.ParseRange("500", "500", out lo, out hi) == null && hi == 500, "exactly 500 is fine");
                Ok(Filters.ParseRange("500", "200", out lo, out hi) == "At most: 500 or more, or empty", "At most below At least: says why");
                Ok(Filters.ParseRange("", "0", out lo, out hi) == "At most: 1 or more, or empty", "At most 0: says why");
                Ok(Filters.ParseRange("abc", "", out lo, out hi).StartsWith("At least:") && Filters.ParseRange("1", "200000", out lo, out hi).StartsWith("At most:"), "bad numbers: says which field");
            }
            // Wallhaven sorted by favourites (Most favourited): its own range and order; the other filters are shared.
            {
                var tf = new Filters { Fav = 200 }.Clean();
                Ok(tf.ListQuery() == "range=1y&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=200&maxw=0", "the toplist's list query: a minimum only: " + tf.ListQuery());
                var mf = new MostFav { Fav = 200, FavMax = 1000 }.Clean();
                Ok(tf.MostFavQuery(mf) == "sort=fav&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=200&favmax=1000&maxw=0", "Most favourited's query, the service's mostfavQueryFor: " + tf.MostFavQuery(mf));
                Ok(tf.MostFavQuery(new MostFav()) == "sort=fav&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=250&maxw=0", "no upper limit: no favmax in it");
                Ok(new MostFav { Fav = 2000, FavMax = 1000 }.Clean().FavMax == 0 && new MostFav { FavMax = -3 }.Clean().FavMax == 0 && new MostFav { FavMax = 100001 }.Clean().FavMax == 0 && new MostFav { Fav = 500, FavMax = 500 }.Clean().FavMax == 500, "a maximum below the minimum or out of range: no upper limit; equal is fine");
                Ok(new MostFav { Order = "fav" }.Clean().Order == "top" && new MostFav { Fav = -1 }.Clean().Fav == 250, "an unknown order or a bad minimum: the defaults");
                Ok(new MostFav { Fav = 500, FavMax = 1000 }.Summary == "500 to 1,000 favourites" && new MostFav { Order = "shuffle" }.Summary == "250+ favourites\u00a0· Shuffled", "the row: the range, and Shuffled only when shuffled");
                var dsr = new DeviceSettings { Sources = new List<string> { "toplist" }, Sort = "fav", MostFav = new MostFav { Fav = 200, FavMax = 1000, Order = "shuffle" } };
                var dsr2 = DeviceSettings.FromDict(Json.Parse(Json.Write(dsr.ToDict())) as Dictionary<string, object>);
                Ok(dsr2.Sort == "fav" && dsr2.MostFav.Same(dsr.MostFav) && dsr2.Signature == dsr.Signature, "the sort and Most favourited go to the service and back");
                Ok(new DeviceSettings().Sort == "toplist" && DeviceSettings.FromDict(Json.Parse("{\"sort\":\"newest\"}") as Dictionary<string, object>).Sort == "toplist", "sorted as the toplist unless it says otherwise");
                var mid = DeviceSettings.FromDict(Json.Parse("{\"sources\":[\"mostfav\",\"fav\"],\"sort\":\"toplist\"}") as Dictionary<string, object>);
                Ok(mid.Sources.SequenceEqual(new[] { "toplist", "fav" }) && mid.Sort == "fav", "Most favourited as a source (2.11 / 1.21): Wallhaven sorted by favourites: " + string.Join(",", mid.Sources) + " " + mid.Sort);
                var both2 = DeviceSettings.FromDict(Json.Parse("{\"sources\":[\"toplist\",\"mostfav\"]}") as Dictionary<string, object>);
                Ok(both2.Sources.SequenceEqual(new[] { "toplist" }) && both2.Sort == "fav", "both on: Wallhaven once, by favourites");
                // the Wallhaven row: the same words as the remote's and the TV's
                Ok(Filters.WallhavenSummary("toplist", new Filters(), new MostFav()) == "Toplist\u00a0· Past year", "sorted as the toplist: " + Filters.WallhavenSummary("toplist", new Filters(), new MostFav()));
                Ok(Filters.WallhavenSummary("fav", new Filters(), new MostFav()) == "Most favourited\u00a0· 250+", "by favourites: the range in few words");
                Ok(Filters.WallhavenSummary("fav", new Filters { Topic = "nature" }, new MostFav { Fav = 500, FavMax = 1000, Order = "shuffle" }) == "Most favourited\u00a0· 500 to 1,000\u00a0· Shuffled\u00a0· “nature”", "Shuffled and a topic when there are");
                Ok(Filters.WallhavenSummary("fav", new Filters(), new MostFav { Fav = 0, FavMax = 1000 }) == "Most favourited\u00a0· Up to 1,000" && Filters.WallhavenSummary("fav", new Filters(), new MostFav { Fav = 0 }) == "Most favourited", "Up to; any range: just the sort");
                var old = DeviceSettings.FromDict(Json.Parse("{\"filters\":{\"fav\":50,\"favmax\":300},\"order\":\"fav\"}") as Dictionary<string, object>);
                Ok(old.MostFav.Same(new MostFav()) && !old.ToDict().ContainsKey("order") && !Json.Write(old.ToDict()).Contains("favmax\":300"), "settings from round 1 (a toplist maximum, a whole-mix order): dropped, Most favourited's defaults");
            }
            // Most first: the most favourited one not shown this round, round after round; shuffled stays random.
            {
                var favOf = new Dictionary<string, int> { { "a", 10 }, { "b", 900 }, { "c", 50 }, { "d", 900 }, { "e", -1 } };
                var pool = new List<string> { "a", "b", "c", "d", "e" };
                var ro = new Rotation(new string[0], new string[0], 7);
                var seen = new List<string>();
                for (int i = 0; i < 7; i++) { string id = ro.PickNew(pool, seen.Count > 0 ? seen.Last() : null, k => favOf[k]); ro.ShownNew(id, pool); seen.Add(id); }
                Ok(string.Join("", seen) == "bdcaebd", "most favourited first, ties in the pool's order, your own pictures last, then a new round from the top: " + string.Join("", seen));
                var picks = new HashSet<string>();
                for (int i = 0; i < 20; i++) picks.Add(new Rotation(new string[0], new string[0], i + 1).PickNew(pool, null));
                Ok(picks.Count > 2, "shuffled picks vary (" + picks.Count + " different first picks)");
            }

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

                // Never show while going back (a change under way, so nothing is shown here): gone from the history,
                // the place kept so the queued Next carries on with the one after it, not a new one at the end
                {
                    var rot = GetF(app, "rot");
                    var hist = (List<string>)rot.GetType().GetField("History").GetValue(rot);
                    hist.Clear(); hist.AddRange(new[] { "def456", "abc123", "def456" });
                    rot.GetType().GetField("Back").SetValue(rot, 1);
                    SetF(app, "current", ((List<Wall>)GetF(app, "walls")).First(x => x.Id == "abc123"));
                    SetF(app, "busy", true);
                    SetF(app, "queued", 0);
                    app.Block();
                    Ok(!hist.Contains("abc123") && (int)rot.GetType().GetField("Back").GetValue(rot) == 0 && (int)GetF(app, "queued") == 1,
                        "Never show while going back: out of the history, and Next goes on to the one after it (history " + string.Join(",", hist) + ")");
                    Ok(!(bool)GetP(app, "CanGoBack") || hist[0] != "abc123", "Previous can't land on it");
                    app.UnhideAll();
                    SetF(app, "busy", false); SetF(app, "queued", 0);
                }
                // the hidden grid: lists them; "Show again" shows only the ones picked; the dialog counts what's picked
                {
                    Call(app, "Change", D("op", "add", "pl", "hidden", "item", D("id", "def456")));
                    Call(app, "Change", D("op", "add", "pl", "hidden", "item", D("id", "zz2222")));
                    var ones = app.HiddenOnes();
                    Ok(ones.Count >= 2 && ones.Any(x => x.Id == "def456") && ones.Any(x => x.Id == "zz2222") && ones.First(x => x.Id == "def456").File != null,
                        "the hidden grid lists them (the cached original for one that's downloaded)");
                    var dlg = (Form)Activator.CreateInstance(typeof(HiddenDialog), All, null, new object[] { ones }, null);
                    var tl = (System.Collections.IList)GetF(dlg, "tiles");
                    var okb = (TextButton)GetF(dlg, "ok");
                    Ok(tl.Count == ones.Count && !okb.Enabled && okb.Text == "Show again", "the grid: a tile each, nothing picked yet, Show again greyed");
                    ((HiddenTile)tl[0]).Selected = true; Call(dlg, "Update");
                    Ok(okb.Enabled && okb.Text == "Show 1 again", "picking one: the button says how many (" + okb.Text + ")");
                    dlg.Close(); dlg.Dispose();
                    app.Unhide(new[] { "def456" });
                    Ok(!d.Hidden.Contains("def456") && d.Hidden.Contains("zz2222"), "Show again: only the ones picked");
                    app.UnhideAll();
                }
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

                // Wallhaven ▸ (sorted as the toplist): Sort by, Period, Popularity, then the shared filters
                Func<string> topLevel = () =>
                {
                    using (var menu = new FluentMenu())
                    {
                        app.FillWallhavenMenu(menu, null);
                        return string.Join("|", menu.Items.Cast<ToolStripItem>().Select(x => x is ToolStripSeparator ? "-" : x.Text));
                    }
                };
                app.SetSort("toplist");
                Ok(topLevel() == "Sort by|-|Period|Popularity|-|Quality|Shape|Categories|Topic|Largest size|-|Turn Wallhaven on", "Wallhaven (off here), sorted as the toplist: " + topLevel());
                // Popularity: the presets, a typed-in one in its place, and Type one…
                {
                    Func<List<ToolStripItem>> popularity = () =>
                    {
                        var menu = new FluentMenu();
                        app.FillWallhavenMenu(menu, null);
                        var pop = menu.Items.OfType<ToolStripMenuItem>().First(x => x.Text == "Popularity");
                        menu.Show(new Point(-3000, -3000));
                        for (int k = 0; k < 10; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                        pop.ShowDropDown();
                        for (int k = 0; k < 10; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                        var items = pop.DropDownItems.Cast<ToolStripItem>().ToList();
                        menu.Close();
                        return items;
                    };
                    int was = s.Filters.Fav;
                    var popA = popularity();
                    Ok(popA.Last().Text == "Type one…" && popA[popA.Count - 2] is ToolStripSeparator && popA.OfType<ToolStripMenuItem>().Count(x => x.Checked) == 1 && !popA.Any(x => x.Text.Contains(" to ")), "the toplist's Popularity: minimums, then Type one…");
                    app.SetFilters(x => x.Fav = 1500);
                    var popB = popularity().OfType<ToolStripMenuItem>().Select(x => x.Text + (x.Checked ? "*" : "")).ToList();
                    Ok(popB.IndexOf("1,500+ favourites*") == popB.IndexOf("1,000+ favourites") + 1, "a typed-in one shows in its place, checked: " + string.Join(" | ", popB));
                    popularity().OfType<ToolStripMenuItem>().First(x => x.Text == "50+ favourites").PerformClick();
                    for (int k = 0; k < 20 && s.Filters.Fav != 50; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                    Ok(s.Filters.Fav == 50 && Settings.Load().Filters.Fav == 50, "picking a preset saves it");
                    app.SetFilters(x => x.Fav = was);
                }

                // Sorted by favourites: Popularity (presets, a typed range in its place, Type a range…) and Order
                {
                    Func<string, List<ToolStripItem>> mostFavMenu = title =>
                    {
                        var menu = new FluentMenu();
                        app.FillWallhavenMenu(menu, null);
                        var top = menu.Items.OfType<ToolStripMenuItem>().First(x => x.Text == title);
                        menu.Show(new Point(-3000, -3000));
                        for (int k = 0; k < 10; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                        top.ShowDropDown();
                        for (int k = 0; k < 10; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                        var items = top.DropDownItems.Cast<ToolStripItem>().ToList();
                        menu.Close();
                        return items;
                    };
                    Func<List<ToolStripItem>, string> texts = l => string.Join("|", l.OfType<ToolStripMenuItem>().Select(x => x.Text + (x.Checked ? "*" : "")));
                    var sortBy = mostFavMenu("Sort by");
                    Ok(texts(sortBy) == "Toplist*|Most favourited", "Sort by: " + texts(sortBy));
                    sortBy.OfType<ToolStripMenuItem>().First(x => x.Text == "Most favourited").PerformClick();
                    for (int k = 0; k < 20 && s.Sort != "fav"; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                    Ok(s.Sort == "fav" && Settings.Load().Sort == "fav", "picking Most favourited sorts by favourites, and settings.ini keeps it");
                    Ok(topLevel() == "Sort by|-|Popularity|Order|-|Quality|Shape|Categories|Topic|Largest size|-|Turn Wallhaven on", "sorted by favourites, its own rows replace Period and the minimum: " + topLevel());
                    var mp = mostFavMenu("Popularity");
                    Ok(texts(mp) == "100+ favourites|250+ favourites*|500+ favourites|1,000+ favourites|2,000+ favourites|Type a range…" && mp[mp.Count - 2] is ToolStripSeparator, "Most favourited's Popularity: " + texts(mp));
                    app.SetMostFav(x => { x.Fav = 500; x.FavMax = 1000; });
                    var mp2 = texts(mostFavMenu("Popularity"));
                    Ok(mp2.Contains("250+ favourites|500 to 1,000 favourites*|500+ favourites"), "a range shows in its place (narrower first, as on the remote), checked: " + mp2);
                    Ok(Settings.Load().MostFav.Fav == 500 && Settings.Load().MostFav.FavMax == 1000, "settings.ini keeps the range");
                    var mo = mostFavMenu("Order");
                    Ok(texts(mo) == "Most first*|Shuffled", "Order: " + texts(mo));
                    mo.OfType<ToolStripMenuItem>().First(x => x.Text == "Shuffled").PerformClick();
                    for (int k = 0; k < 20 && s.MostFav.Order != "shuffle"; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                    Ok(s.MostFav.Order == "shuffle" && Settings.Load().MostFav.Order == "shuffle", "Shuffled is saved");
                    var turnOn = new FluentMenu();
                    var wasSources = s.Sources.ToList();
                    s.Sources = new List<string> { "fav" };
                    app.FillWallhavenMenu(turnOn, null);
                    Ok(turnOn.Items.Cast<ToolStripItem>().Last().Text == "Turn Wallhaven on", "while it's off, the menu offers to turn it on");
                    s.Sources = wasSources;
                    turnOn.Dispose();

                    // its list joins the pool: inside the range, no duplicates, hidden left out; Most first from the top
                    var oldSources = s.Sources.ToList();
                    Func<string, int, Wall> mk = (id, favN) => Wall.Make(id, "https://w.wallhaven.cc/full/" + id.Substring(0, 2) + "/wallhaven-" + id + ".jpg", "https://wallhaven.cc/w/" + id, "3840x2160", favN);
                    SetF(app, "favWalls", new List<Wall> { mk("mf0001", 1200), mk("mf0002", 900), mk("mf0003", 700), mk("mf0004", 450), mk("abc123", 600) });
                    SetF(app, "walls", new List<Wall> { mk("abc123", 600), mk("tp0001", 80) });
                    app.SetMostFav(x => x.Order = "top");
                    s.Sources = new List<string> { "toplist" };
                    var mfPool = ((List<Wall>)Call(app, "Pool")).Select(w => w.Id).OrderBy(x => x).ToList();
                    Ok(string.Join(",", mfPool) == "abc123,mf0002,mf0003", "by favourites, only Most favourited's list, in 500 to 1,000: " + string.Join(",", mfPool));
                    var srcInfo = app.SourceByKey("toplist");
                    Ok(srcInfo != null && srcInfo.Name == "Wallhaven" && srcInfo.Count == 3 && app.Sources()[0].Key == "toplist" && app.Sources()[1].Key == "fav" && app.SourceByKey("mostfav") == null, "Shuffle from: Wallhaven, with its list's count, then Favourites");
                    Ok(app.SourcesSummary == "Wallhaven", "Shuffle from reads Wallhaven");
                    SetF(app, "current", null);
                    var first = (Wall)Call(app, "PickNext");
                    Ok(first != null && first.Id == "mf0002", "Most first: the most favourited first: " + (first == null ? "none" : first.Id));
                    var mst = (Dictionary<string, object>)Call(app, "StatusForRemote", new object[] { null });
                    Ok(!mst.ContainsKey("list") && !((Dictionary<string, object>)mst["counts"]).ContainsKey("toplist"), "no list or count for the remote before Most favourited's list for this range is here");
                    SetF(app, "favFor", s.Filters.MostFavQuery(s.MostFav)); SetF(app, "favOf", 4800);
                    mst = (Dictionary<string, object>)Call(app, "StatusForRemote", new object[] { null });
                    var counts = (Dictionary<string, object>)mst["counts"];
                    Ok((string)mst["list"] == s.Filters.MostFavQuery(s.MostFav) && (int)counts["toplist"] == 3 && (int)counts["toplistOf"] == 4800 && !mst.ContainsKey("listFav") && !counts.ContainsKey("mostfav"), "the remote gets its query, count and the range's size, as Wallhaven's");
                    app.SetSort("toplist");
                    var tpPool = ((List<Wall>)Call(app, "Pool")).Select(w => w.Id).OrderBy(x => x).ToList();
                    Ok(string.Join(",", tpPool) == "abc123,tp0001", "sorted as the toplist again: only the toplist's list: " + string.Join(",", tpPool));
                    mst = (Dictionary<string, object>)Call(app, "StatusForRemote", new object[] { null });
                    counts = (Dictionary<string, object>)mst["counts"];
                    Ok((string)mst["list"] == (string)GetF(app, "listFor") && (int)counts["toplist"] == 2 && !counts.ContainsKey("toplistOf"), "and reports the toplist's");
                    // settings.ini from 2.11 with Most favourited on: Wallhaven sorted by favourites
                    s.Sources = new List<string> { "mostfav", "fav" }; s.Save();
                    var ini = Settings.Load();
                    Ok(ini.Sources.SequenceEqual(new[] { "toplist", "fav" }) && ini.Sort == "fav", "settings.ini's mostfav source becomes Wallhaven sorted by favourites: " + string.Join("|", ini.Sources) + " " + ini.Sort);
                    s.Sources = oldSources; s.Sort = "toplist"; s.Save();
                    app.SetMostFav(x => { x.Fav = 250; x.FavMax = 0; x.Order = "top"; });

                    // the window's row: a long value widens its button (never cut while the name leaves room), a short one shrinks it back
                    using (var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false, ClientSize = new Size((int)(424 * Theme.Scale), (int)(60 * Theme.Scale)) })
                    {
                        var btn = new MenuButton();
                        var row = new Row("Wallhaven", btn, false) { Bounds = new Rectangle(0, 0, (int)(422 * Theme.Scale), (int)(56 * Theme.Scale)) };
                        form.Controls.Add(row);
                        btn.Text = Filters.WallhavenSummary("fav", new Filters { Topic = "night city" }, new MostFav { Fav = 500, FavMax = 1000, Order = "shuffle" });   // set before it has a handle, as the window does
                        form.Show();
                        for (int k = 0; k < 10; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                        float text, name;
                        using (var gr = btn.CreateGraphics()) { text = Theme.Measure(gr, btn.Text, btn.Font).Width; name = Theme.Measure(gr, row.Text, row.Font).Width; }
                        int wide = btn.Width;
                        Ok(wide - 48 * Theme.Scale >= text && btn.Left >= 20 * Theme.Scale + name, "a long value fits its button (" + wide + " wide for " + Math.Ceiling(text) + ") and leaves the name whole");
                        btn.Text = "2 minutes";
                        Application.DoEvents();
                        Ok(btn.Width == (int)Math.Round(200 * Theme.Scale), "a short value: back to the usual width (" + btn.Width + ")");
                        form.Close();
                    }
                    SetF(app, "favWalls", new List<Wall>()); SetF(app, "favFor", ""); SetF(app, "favOf", 0);
                }
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
            }            // The same for a choice inside a submenu of such a menu (the window's Wallhaven > Quality, Change every, ...).
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
                Ok(choice != null && ran, "a choice in a submenu of such a menu runs too (the window's Wallhaven, Change every)");
                host.Dispose();
            }            Console.WriteLine(fails == 0 ? "ALL PASSED (" + passes + ")" : fails + " FAILED, " + passes + " passed");
            return fails;
        }
    }
}
