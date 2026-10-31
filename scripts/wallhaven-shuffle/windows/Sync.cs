// Sync with the wallhaven-shuffle service (api/sync): the shared playlists, this PC's settings (the remote and the TV
// can change them too), what it shows, and commands from the remote. The model and its rules here mirror the
// service's lib/sync.js; the app (TrayApp) keeps the folders and the shuffle in step with them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace WallhavenShuffle
{
    // ==================================================================================================== choices

    // The toplist filters, with the same choices and words as the remote and the TV.
    class Filters
    {
        public string Quality = "4k+", Ratio = "16x9,16x10", Range = "1y", Cats = "110", Topic = "";
        public int Fav = 50, MaxWidth = 0;

        public static readonly string[][] Qualities = {
            new[] { "1080+", "Full HD and up", "1920x1080" }, new[] { "1440+", "1440p and up", "2560x1440" },
            new[] { "4k+", "4K and up", "3840x2160" }, new[] { "4k", "Exactly 4K", "=3840x2160,3840x2400" },
            new[] { "5k+", "5K and up", "5120x2880" }, new[] { "8k", "8K", "7680x4320" } };
        public static readonly string[][] Ratios = {
            new[] { "16x9,16x10", "Widescreen" }, new[] { "16x9", "16:9 (TV)" }, new[] { "16x10", "16:10 (laptop)" },
            new[] { "21x9,32x9", "Ultrawide" }, new[] { "", "Any" } };
        public static readonly string[][] Ranges = {
            new[] { "1d", "Past day" }, new[] { "3d", "Past 3 days" }, new[] { "1w", "Past week" }, new[] { "1M", "Past month" },
            new[] { "3M", "Past 3 months" }, new[] { "6M", "Past 6 months" }, new[] { "1y", "Past year" } };
        public static readonly int[] Favs = { 0, 10, 25, 50, 100, 250, 500, 1000 };
        public static readonly int[] Widths = { 0, 3840, 5120 };
        public static readonly int[] Every = { 1, 2, 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440 };
        public static readonly string[] Topics = { "nature", "landscape", "mountains", "forest", "ocean", "space", "city", "night", "sunset", "winter", "architecture", "minimalism", "abstract", "digital art", "cars" };

        public static string Label(string[][] table, string key) { var row = table.FirstOrDefault(r => r[0] == key); return row != null ? row[1] : key; }
        public static string FavLabel(int n) { return n == 0 ? "Any" : n.ToString("N0") + "+ favourites"; }
        public static string WidthLabel(int w) { return w == 0 ? "Any" : w == 3840 ? "Up to 4K" : "Up to 5K"; }
        public static string EveryLabel(int m)
        {
            if (m < 60) return m == 1 ? "1 minute" : m + " minutes";
            if (m < 1440) return m == 60 ? "1 hour" : (m / 60) + " hours";
            return "1 day";
        }
        public string CatsLabel { get { var names = new[] { "General", "Anime", "People" }; return string.Join(", ", Enumerable.Range(0, 3).Where(i => Cats[i] == '1').Select(i => names[i])); } }
        public string Summary { get { return Label(Qualities, Quality) + " · " + Label(Ranges, Range) + (Topic.Length > 0 ? " · “" + Topic + "”" : ""); } }

        public static string NormTopic(string q)
        {
            string t = Regex.Replace((q ?? "").ToLowerInvariant(), @"\s+", " ").Trim();
            return Regex.IsMatch(t, "^[a-z0-9 -]{1,40}$") ? t : "";
        }

        public Filters Clean()
        {
            if (!Qualities.Any(r => r[0] == Quality)) Quality = "4k+";
            if (!Ratios.Any(r => r[0] == Ratio)) Ratio = "16x9,16x10";
            if (!Ranges.Any(r => r[0] == Range)) Range = "1y";
            if (Cats == null || !Regex.IsMatch(Cats, "^[01]{3}$") || Cats == "000") Cats = "110";
            Topic = NormTopic(Topic);
            if (Fav < 0 || Fav > 100000) Fav = 50;
            if (!Widths.Contains(MaxWidth)) MaxWidth = 0;
            return this;
        }

        public Filters Copy() { return (Filters)MemberwiseClone(); }

        // The /api/list query: character for character the service's listQueryFor, so every device with the same
        // filters shares one cached list. No width limit in it (each app applies its own).
        public string ListQuery()
        {
            string size = Qualities.First(r => r[0] == Quality)[2];
            var b = new StringBuilder("range=" + Range);
            b.Append(size.StartsWith("=") ? "&res=" + size.Substring(1) : "&atleast=" + size);
            if (Ratio.Length > 0) b.Append("&ratios=" + Ratio);
            b.Append("&cats=" + Cats + "&purity=100");
            if (Topic.Length > 0) b.Append("&q=" + Uri.EscapeDataString(Topic));
            b.Append("&fav=" + Fav + "&maxw=0");
            return b.ToString();
        }

        // The filters from an old-style list address (ListUrl), for moving to filters.
        public static Filters FromListUrl(string url)
        {
            var f = new Filters();
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return f;
            var q = System.Web.HttpUtility.ParseQueryString(u.Query);
            if (q["range"] != null) f.Range = q["range"];
            if (q["atleast"] != null) { var row = Qualities.FirstOrDefault(r => r[2] == q["atleast"]); if (row != null) f.Quality = row[0]; }
            if (q["ratios"] != null) f.Ratio = q["ratios"];
            if (q["cats"] != null) f.Cats = q["cats"];
            int n;
            if (q["fav"] != null && int.TryParse(q["fav"], out n)) f.Fav = n;
            if (q["maxw"] != null && int.TryParse(q["maxw"], out n)) f.MaxWidth = n;
            if (q["q"] != null) f.Topic = q["q"];
            return f.Clean();
        }
    }

    // ==================================================================================================== the document

    class SyncItem
    {
        public string Id, Ext = "jpg", Res = "";
        public int Fav;
        public long At;
        public string Image { get { return "https://w.wallhaven.cc/full/" + Id.Substring(0, 2) + "/wallhaven-" + Id + "." + Ext; } }
        public string Page { get { return "https://wallhaven.cc/w/" + Id; } }
        public SyncItem Copy() { return (SyncItem)MemberwiseClone(); }
    }

    class SyncPlaylist
    {
        public string Id, Name, Kind = "";   // Kind: "fav", "hidden", or "" for your own
        public List<SyncItem> Items = new List<SyncItem>();
        public bool Has(string id) { return Items.Any(x => x.Id == id); }
    }

    // Shared playlists and this device's settings: the service's document, or (not paired) this PC's own copy, with
    // the same rules for every change.
    class SyncDoc
    {
        public int V;
        public List<SyncPlaylist> Playlists = new List<SyncPlaylist>();
        public Dictionary<string, Dictionary<string, object>> Devices = new Dictionary<string, Dictionary<string, object>>();
        public const int MaxItems = 5000, MaxPlaylists = 100;
        static readonly Regex WallId = new Regex("^[a-z0-9]{6}$");   // Wallhaven ids: six lower-case letters and digits

        public SyncDoc() { EnsureBuiltins(); }

        void EnsureBuiltins()
        {
            if (Find("hidden") == null) Playlists.Insert(0, new SyncPlaylist { Id = "hidden", Name = "Hidden", Kind = "hidden" });
            if (Find("fav") == null) Playlists.Insert(0, new SyncPlaylist { Id = "fav", Name = "Favourites", Kind = "fav" });
        }

        public SyncPlaylist Find(string id) { return Playlists.FirstOrDefault(p => p.Id == id); }
        public IEnumerable<SyncPlaylist> Own { get { return Playlists.Where(p => p.Kind == ""); } }
        public HashSet<string> Hidden { get { return new HashSet<string>(Find("hidden").Items.Select(x => x.Id)); } }

        public static bool IsWallId(string id) { return id != null && WallId.IsMatch(id); }

        public static SyncItem MakeItem(string id, string ext, string res, int fav)
        {
            id = (id ?? "").ToLowerInvariant();
            if (!IsWallId(id)) return null;
            ext = (ext ?? "").TrimStart('.').ToLowerInvariant();
            return new SyncItem { Id = id, Ext = ext == "png" ? "png" : "jpg", Res = Regex.IsMatch(res ?? "", @"^\d{3,5}x\d{3,5}$") ? res : "", Fav = Math.Max(0, fav), At = Now() };
        }

        static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; }

        // The Windows rules (a playlist is a folder here). Why the name can't be used, or null.
        public string CheckName(string name, string exceptId)
        {
            string n = Regex.Replace(name ?? "", @"\s+", " ").Trim();
            if (n.Length == 0) return "Type a name";
            if (n.Length > 60) return "Keep it under 60 characters";
            if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A name can't contain \\ / : * ? \" < > |";
            if (n.EndsWith(".")) return "A name can't end with a dot";
            if (Regex.IsMatch(n.Split('.')[0], "^(con|prn|aux|nul|com[1-9]|lpt[1-9])$", RegexOptions.IgnoreCase)) return "Windows reserves that name";
            if (Playlists.Any(p => p.Id != exceptId && string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))) return "There's already a playlist called " + n;
            return null;
        }

        public static string NewId()
        {
            const string a = "abcdefghijkmnpqrstuvwxyz23456789";
            var bytes = new byte[10];
            using (var r = System.Security.Cryptography.RandomNumberGenerator.Create()) r.GetBytes(bytes);
            return "p_" + new string(bytes.Select(b => a[b % 32]).ToArray());
        }

        // One change ({op: add|remove|create|rename|delete|clear|settings|name}), as the service makes it. Null when
        // done (or already so), else why not.
        public string Apply(Dictionary<string, object> op)
        {
            string kind = Str(op, "op");
            switch (kind)
            {
                case "add":
                {
                    var p = Find(Str(op, "pl"));
                    var it = ItemFrom(op.ContainsKey("item") ? op["item"] as Dictionary<string, object> : null);
                    if (p == null) return "That playlist is gone";
                    if (it == null) return "Only Wallhaven wallpapers can be synced";
                    if (!p.Has(it.Id)) { if (p.Items.Count >= MaxItems) return "\"" + p.Name + "\" is full"; p.Items.Insert(0, it); }
                    if (p.Kind != "hidden") Find("hidden").Items.RemoveAll(x => x.Id == it.Id);
                    return null;
                }
                case "remove": { var p = Find(Str(op, "pl")); if (p != null) p.Items.RemoveAll(x => x.Id == Str(op, "id").ToLowerInvariant()); return null; }
                case "clear": { var p = Find(Str(op, "pl")); if (p != null) p.Items.Clear(); return null; }
                case "create":
                {
                    string id = Str(op, "id");
                    if (!Regex.IsMatch(id, "^p_[a-z0-9]{4,24}$")) return "That playlist couldn't be made (bad id)";
                    if (Find(id) != null) return null;
                    if (Playlists.Count >= MaxPlaylists + 2) return "That's the most playlists there can be";
                    string why = CheckName(Str(op, "name"), null);
                    if (why != null) return why;
                    var items = new List<SyncItem>();
                    var arr = op.ContainsKey("items") ? op["items"] as object[] : null;
                    if (arr == null && op.ContainsKey("items") && op["items"] is System.Collections.ArrayList) arr = ((System.Collections.ArrayList)op["items"]).ToArray();
                    if (arr != null) foreach (var o in arr) { var it = ItemFrom(o as Dictionary<string, object>); if (it != null && !items.Any(x => x.Id == it.Id)) items.Add(it); }
                    Playlists.Add(new SyncPlaylist { Id = id, Name = Regex.Replace(Str(op, "name"), @"\s+", " ").Trim(), Items = items });
                    var ids = new HashSet<string>(items.Select(x => x.Id));
                    Find("hidden").Items.RemoveAll(x => ids.Contains(x.Id));
                    return null;
                }
                case "rename":
                {
                    var p = Find(Str(op, "pl"));
                    if (p == null) return "That playlist is gone";
                    if (p.Kind != "") return "\"" + p.Name + "\" can't be renamed";
                    string why = CheckName(Str(op, "name"), p.Id);
                    if (why != null) return why;
                    p.Name = Regex.Replace(Str(op, "name"), @"\s+", " ").Trim();
                    return null;
                }
                case "delete":
                {
                    var p = Find(Str(op, "pl"));
                    if (p == null) return null;
                    if (p.Kind != "") return "\"" + p.Name + "\" can't be deleted";
                    Playlists.Remove(p);
                    return null;
                }
                case "settings": case "name": return null;   // other devices' business; this PC's own settings are applied by the app
                default: return "Unknown change";
            }
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }
        static int Int(Dictionary<string, object> d, string k) { object v; try { return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToInt32(v) : 0; } catch (Exception) { return 0; } }

        static SyncItem ItemFrom(Dictionary<string, object> d)
        {
            if (d == null) return null;
            var it = MakeItem(Str(d, "id"), Str(d, "ext"), Str(d, "res"), Int(d, "fav"));
            if (it == null) return null;
            object at;
            if (d.TryGetValue("at", out at) && at != null) try { long t = Convert.ToInt64(at); if (t > 1500000000) it.At = t; } catch (Exception) { }
            return it;
        }

        public static Dictionary<string, object> ItemDict(SyncItem it)
        {
            return new Dictionary<string, object> { { "id", it.Id }, { "ext", it.Ext }, { "res", it.Res }, { "fav", it.Fav }, { "at", it.At } };
        }

        public static Dictionary<string, object> Op(string op, params object[] kv)
        {
            var d = new Dictionary<string, object> { { "op", op } };
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        // ---- JSON

        public static SyncDoc FromDict(Dictionary<string, object> root)
        {
            var doc = new SyncDoc { Playlists = new List<SyncPlaylist>() };
            if (root == null) { doc.EnsureBuiltins(); return doc; }
            doc.V = Int(root, "v");
            object pls;
            if (root.TryGetValue("playlists", out pls) && pls is object[])
                foreach (var o in (object[])pls)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var p = new SyncPlaylist { Id = Str(d, "id"), Name = Str(d, "name"), Kind = Str(d, "kind") };
                    object items;
                    if (d.TryGetValue("items", out items) && items is object[])
                        foreach (var i in (object[])items) { var it = ItemFrom(i as Dictionary<string, object>); if (it != null) p.Items.Add(it); }
                    if (p.Id.Length > 0) doc.Playlists.Add(p);
                }
            object devs;
            if (root.TryGetValue("devices", out devs) && devs is Dictionary<string, object>)
                foreach (var kv in (Dictionary<string, object>)devs)
                    if (kv.Value is Dictionary<string, object>) doc.Devices[kv.Key] = (Dictionary<string, object>)kv.Value;
            doc.EnsureBuiltins();
            return doc;
        }

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object> {
                { "v", V },
                { "playlists", Playlists.Select(p => (object)new Dictionary<string, object> { { "id", p.Id }, { "name", p.Name }, { "kind", p.Kind }, { "items", p.Items.Select(i => (object)ItemDict(i)).ToArray() } }).ToArray() },
                { "devices", Devices.ToDictionary(kv => kv.Key, kv => (object)kv.Value) } };
        }

        public SyncDoc Copy() { return FromDict(Json.Parse(Json.Write(ToDict()))); }
    }

    // This PC's settings as the service keeps them (doc.devices[id].settings).
    class DeviceSettings
    {
        public List<string> Sources = new List<string>();
        public Filters Filters = new Filters();
        public int Every = 15;
        public bool Paused;
        public string Keep = "";

        public Dictionary<string, object> ToDict()
        {
            var f = Filters;
            return new Dictionary<string, object> {
                { "sources", Sources.ToArray() },
                { "filters", new Dictionary<string, object> { { "quality", f.Quality }, { "ratio", f.Ratio }, { "range", f.Range }, { "cats", f.Cats }, { "q", f.Topic }, { "fav", f.Fav }, { "maxw", f.MaxWidth } } },
                { "every", Every }, { "paused", Paused }, { "keep", Keep } };
        }

        public static DeviceSettings FromDict(Dictionary<string, object> d)
        {
            var s = new DeviceSettings();
            if (d == null) return null;
            object v;
            if (d.TryGetValue("sources", out v) && v is object[]) s.Sources = ((object[])v).Select(Convert.ToString).Where(k => Regex.IsMatch(k ?? "", "^(toplist|fav|p_[a-z0-9]{4,24})$")).Distinct().ToList();
            if (d.TryGetValue("filters", out v) && v is Dictionary<string, object>)
            {
                var f = (Dictionary<string, object>)v;
                Func<string, string, string> str = (k, def) => { object x; return f.TryGetValue(k, out x) && x != null ? Convert.ToString(x) : def; };
                Func<string, int, int> num = (k, def) => { object x; try { return f.TryGetValue(k, out x) && x != null ? Convert.ToInt32(x) : def; } catch (Exception) { return def; } };
                s.Filters = new Filters { Quality = str("quality", "4k+"), Ratio = str("ratio", "16x9,16x10"), Range = str("range", "1y"), Cats = str("cats", "110"), Topic = str("q", ""), Fav = num("fav", 50), MaxWidth = num("maxw", 0) }.Clean();
            }
            try { if (d.TryGetValue("every", out v)) s.Every = Math.Max(1, Math.Min(1440, Convert.ToInt32(v))); } catch (Exception) { }
            if (d.TryGetValue("paused", out v) && v is bool) s.Paused = (bool)v;
            if (d.TryGetValue("keep", out v) && v != null) s.Keep = Convert.ToString(v);
            return s;
        }

        public string Signature { get { return Json.Write(ToDict()); } }
    }

    static class Json
    {
        static JavaScriptSerializer New() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 }; }
        public static Dictionary<string, object> Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return New().DeserializeObject(json) as Dictionary<string, object>; } catch (Exception) { return null; }
        }
        public static string Write(object o) { return New().Serialize(o); }
    }

    // ==================================================================================================== HTTP

    class SyncError : Exception
    {
        public readonly int Status;
        public SyncError(int status, string message) : base(message) { Status = status; }
    }

    // One request to /api/sync/<action>. Throws SyncError with the service's own words (Status 0 = no connection).
    static class SyncClient
    {
        public const string UserAgent = "WallhavenShuffle-Windows/2.5";

        public static Dictionary<string, object> Call(string server, string action, Dictionary<string, object> body, string token)
        {
            if (string.IsNullOrEmpty(server)) throw new SyncError(0, "There's no service address yet");
            var req = (HttpWebRequest)WebRequest.Create(server.TrimEnd('/') + "/api/sync/" + action);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.UserAgent = UserAgent;
            req.Timeout = 15000; req.ReadWriteTimeout = 15000;
            if (!string.IsNullOrEmpty(token)) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            byte[] data = Encoding.UTF8.GetBytes(Json.Write(body ?? new Dictionary<string, object>()));
            try
            {
                using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return Json.Parse(r.ReadToEnd()) ?? new Dictionary<string, object>();
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null) throw new SyncError(0, "Can't reach the service (" + ex.Status + ")");
                string text = "";
                try { using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) text = r.ReadToEnd(); } catch (Exception) { }
                var j = Json.Parse(text);
                object err = null;
                if (j != null) j.TryGetValue("error", out err);
                throw new SyncError((int)resp.StatusCode, err != null ? Convert.ToString(err) : "The service answered " + (int)resp.StatusCode);
            }
        }
    }
}
