package io.github.muknan.wallhavenshuffle;

import android.content.Context;
import android.content.SharedPreferences;
import android.net.Uri;
import android.os.PowerManager;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.concurrent.CopyOnWriteArrayList;

/**
 * Sync with the service (api/sync): this TV's settings (which the remote and the Windows app can change too), the
 * shared playlists, what the TV shows, and commands from the remote. It works without sync as well: the settings are
 * always kept here, and sync only mirrors them.
 *
 * A background thread checks in every 30 s while the screen is on and the plugin is in use (every 3 s while a remote
 * is open), with one small request; the playlists come down only when they changed.
 */
final class Sync {
    static final String VERSION = "1.13";

    // ---- settings (always local; mirrored to the service when paired)

    private static TvSettings cachedSettings;

    static synchronized TvSettings settings(Context c) {
        if (cachedSettings != null) return cachedSettings.copy();
        SharedPreferences p = ListStore.prefs(c);
        String raw = p.getString("settings_json", "");
        TvSettings s = null;
        if (!raw.isEmpty()) {
            try { s = fromJson(new JSONObject(raw)); } catch (Exception e) { Log.w(ListStore.TAG, "settings unreadable, starting over: " + e); }
        }
        if (s == null) s = migrate(p);
        cachedSettings = s.clean();
        return cachedSettings.copy();
    }

    /** Saves settings. local = changed on this TV (they go to the service at the next check-in). */
    static void saveSettings(Context c, TvSettings s, boolean local) {
        TvSettings before = settings(c);
        s.clean();
        synchronized (Sync.class) {
            cachedSettings = s.copy();
            SharedPreferences.Editor e = ListStore.prefs(c).edit().putString("settings_json", toJson(s).toString());
            if (local) e.putBoolean("settings_dirty", true);
            e.apply();
        }
        settingsChanged(c, before, s);
        if (local) kick(c);
    }

    // What changing settings sets off: a new list for new filters, a new wallpaper for new sources.
    private static void settingsChanged(Context c, TvSettings a, TvSettings b) {
        if (!a.listQuery().equals(b.listQuery()) || !ListStore.server(c).isEmpty() && !ListStore.prefs(c).getString("list_for", "").equals(b.listQuery()))
            ListStore.refreshNow(c, null);
        boolean newPool = !a.sources.equals(b.sources) || a.maxw != b.maxw;
        if (newPool && !b.paused && b.keep.isEmpty()) ShuffleService.skipWait(c);
        if (newPool || a.paused != b.paused || !a.keep.equals(b.keep)) statusChanged(c);
        notifyListeners();
    }

    // Before sync, the settings were a list address and a width: carry those over (the same list as before).
    private static TvSettings migrate(SharedPreferences p) {
        TvSettings s = TvSettings.defaults();
        s.maxw = p.getInt("max_width", 3840) > 0 ? 3840 : 0;
        String url = p.getString("list_url", "");
        if (!url.isEmpty()) {
            try {
                Uri u = Uri.parse(url);
                if (u.getQueryParameter("range") != null) s.range = u.getQueryParameter("range");
                String at = u.getQueryParameter("atleast");
                if (at != null) for (String k : TvSettings.QUALITY_SIZE.keySet()) if (TvSettings.QUALITY_SIZE.get(k).equals(at)) s.quality = k;
                if (u.getQueryParameter("ratios") != null) s.ratio = u.getQueryParameter("ratios");
                if (u.getQueryParameter("cats") != null) s.cats = u.getQueryParameter("cats");
                if (u.getQueryParameter("fav") != null) s.fav = Integer.parseInt(u.getQueryParameter("fav"));
                if (u.getQueryParameter("q") != null) s.q = u.getQueryParameter("q");
            } catch (Exception e) { Log.w(ListStore.TAG, "couldn't read the old list address: " + e); }
        }
        return s;
    }

    static TvSettings fromJson(JSONObject o) {
        TvSettings s = new TvSettings();
        JSONArray src = o.optJSONArray("sources");
        if (src != null) for (int i = 0; i < src.length(); i++) s.sources.add(src.optString(i));
        JSONObject f = o.optJSONObject("filters");
        if (f != null) {
            s.quality = f.optString("quality", s.quality); s.ratio = f.optString("ratio", s.ratio); s.range = f.optString("range", s.range);
            s.cats = f.optString("cats", s.cats); s.q = f.optString("q", ""); s.fav = f.optInt("fav", s.fav); s.maxw = f.optInt("maxw", s.maxw);
        }
        s.every = o.optInt("every", s.every);
        s.paused = o.optBoolean("paused", false);
        s.keep = o.optString("keep", "");
        return s;
    }

    static JSONObject toJson(TvSettings s) {
        try {
            JSONObject f = new JSONObject().put("quality", s.quality).put("ratio", s.ratio).put("range", s.range).put("cats", s.cats)
                .put("q", s.q).put("fav", s.fav).put("maxw", s.maxw);
            return new JSONObject().put("sources", new JSONArray(s.sources)).put("filters", f).put("every", s.every)
                .put("paused", s.paused).put("keep", s.keep);
        } catch (Exception e) { throw new IllegalStateException(e); }
    }

    // ---- the shared document (playlists, devices), cached in doc.json

    private static JSONObject doc;
    private static long docStamp = -1;

    private static File docFile(Context c) { return new File(c.getFilesDir(), "doc.json"); }

    static synchronized JSONObject doc(Context c) {
        File f = docFile(c);
        long stamp = f.exists() ? f.lastModified() * 31 + f.length() : 0;
        if (stamp == docStamp && doc != null) return doc;
        JSONObject d = null;
        if (stamp != 0) {
            try (InputStream in = new FileInputStream(f)) { d = new JSONObject(new String(readAll(in), StandardCharsets.UTF_8)); }
            catch (Exception e) { Log.w(ListStore.TAG, "couldn't read the playlists: " + e); }
        }
        doc = d != null ? d : new JSONObject();
        docStamp = stamp;
        return doc;
    }

    private static synchronized void saveDoc(Context c, JSONObject d) {
        File tmp = new File(c.getFilesDir(), "doc.json.new"), f = docFile(c);
        try (OutputStream out = new FileOutputStream(tmp)) { out.write(d.toString().getBytes(StandardCharsets.UTF_8)); }
        catch (Exception e) { Log.w(ListStore.TAG, "couldn't save the playlists: " + e); return; }
        if (!tmp.renameTo(f)) { f.delete(); tmp.renameTo(f); }
        doc = d;
        docStamp = f.lastModified() * 31 + f.length();
    }

    /** A playlist ("fav", "hidden" or one of yours) from the cached document, or null. */
    static JSONObject playlist(Context c, String id) {
        JSONArray pls = doc(c).optJSONArray("playlists");
        if (pls == null) return null;
        for (int i = 0; i < pls.length(); i++) { JSONObject p = pls.optJSONObject(i); if (p != null && id.equals(p.optString("id"))) return p; }
        return null;
    }

    /** The playlists you made (not Favourites or Hidden), in order. */
    static List<JSONObject> ownPlaylists(Context c) {
        List<JSONObject> out = new ArrayList<>();
        JSONArray pls = doc(c).optJSONArray("playlists");
        if (pls != null) for (int i = 0; i < pls.length(); i++) { JSONObject p = pls.optJSONObject(i); if (p != null && p.optString("kind").isEmpty()) out.add(p); }
        return out;
    }

    static List<Shuffle.Item> items(Context c, String plId) {
        List<Shuffle.Item> out = new ArrayList<>();
        JSONObject p = playlist(c, plId);
        JSONArray items = p == null ? null : p.optJSONArray("items");
        if (items != null) for (int i = 0; i < items.length(); i++) { Shuffle.Item w = item(items.optJSONObject(i)); if (w != null) out.add(w); }
        return out;
    }

    static boolean contains(Context c, String plId, String wallId) {
        for (Shuffle.Item w : items(c, plId)) if (w.id.equals(wallId)) return true;
        return false;
    }

    static Set<String> hiddenIds(Context c) {
        Set<String> out = new HashSet<>();
        for (Shuffle.Item w : items(c, "hidden")) out.add(w.id);
        return out;
    }

    /** A playlist entry ({id, ext, res, fav}) as a wallpaper this plugin can hand Projectivy. */
    static Shuffle.Item item(JSONObject o) {
        if (o == null) return null;
        String id = o.optString("id");
        if (!id.matches("[a-z0-9]{4,12}")) return null;
        String ext = "png".equals(o.optString("ext")) ? "png" : "jpg", res = o.optString("res");
        int width = 0;
        try { width = Integer.parseInt(res.substring(0, res.indexOf('x'))); } catch (Exception ignored) { }
        return new Shuffle.Item(id, "https://w.wallhaven.cc/full/" + id.substring(0, 2) + "/wallhaven-" + id + "." + ext,
            "https://wallhaven.cc/w/" + id, res, width, o.optInt("fav"));
    }

    static JSONObject itemJson(Shuffle.Item w) {
        try {
            return new JSONObject().put("id", w.id).put("ext", w.image.endsWith(".png") ? "png" : "jpg").put("res", w.resolution).put("fav", w.favorites);
        } catch (Exception e) { throw new IllegalStateException(e); }
    }

    // ---- pairing and signing out

    static boolean paired(Context c) { return !ListStore.prefs(c).getString("sync_token", "").isEmpty(); }
    static String deviceName(Context c) { return ListStore.prefs(c).getString("sync_name", ""); }

    /** Asks for a pairing code: {code, claim, expires}. Network: call off the main thread. */
    static JSONObject startPairing(Context c) throws Exception {
        JSONObject b = new JSONObject().put("kind", "tv").put("name", "TV").put("settings", toJson(settings(c)));
        return call(c, "pair", b, false);
    }

    /** True once the code was approved (and this TV is now paired). */
    static boolean claim(Context c, String claim) throws Exception {
        JSONObject r = call(c, "claim", new JSONObject().put("claim", claim), false);
        String token = r.optString("token");
        if (token.isEmpty()) return false;
        ListStore.prefs(c).edit().putString("sync_token", token).putString("sync_id", r.optString("id")).putString("sync_name", r.optString("name"))
            .putInt("doc_v", -1).putInt("cmd_seq", 0).putBoolean("settings_dirty", false).putString("st_hash", "").apply();
        Log.i(ListStore.TAG, "paired as " + r.optString("name"));
        kick(c);
        return true;
    }

    static void signOut(Context c) {
        final String token = ListStore.prefs(c).getString("sync_token", "");
        ListStore.prefs(c).edit().remove("sync_token").remove("sync_id").remove("sync_name").putInt("doc_v", -1).apply();
        notifyListeners();
        if (token.isEmpty()) return;
        new Thread(() -> { try { request(c, "forget", new JSONObject(), token); } catch (Exception ignored) { } }, "sync-signout").start();
    }

    /** Sends changes to the playlists ({op: add|remove|...}); the cached document follows. Off the main thread. */
    static String op(Context c, JSONObject... ops) {
        try {
            JSONArray arr = new JSONArray();
            for (JSONObject o : ops) arr.put(o);
            JSONObject r = call(c, "op", new JSONObject().put("ops", arr), true);
            JSONObject d = r.optJSONObject("doc");
            if (d != null) { saveDoc(c, d); ListStore.prefs(c).edit().putInt("doc_v", d.optInt("v")).apply(); afterDoc(c); }
            JSONArray errors = r.optJSONArray("errors");
            return errors != null && errors.length() > 0 ? errors.optJSONObject(0).optString("error") : null;
        } catch (Exception e) {
            return e.getMessage();
        }
    }

    // ---- the check-in loop

    interface Listener { void changed(); }
    private static final CopyOnWriteArrayList<Listener> listeners = new CopyOnWriteArrayList<>();
    static void listen(Listener l) { listeners.add(l); }
    static void unlisten(Listener l) { listeners.remove(l); }
    private static void notifyListeners() { for (Listener l : listeners) l.changed(); }

    private static final Object loop = new Object();
    private static Thread thread;
    private static int users;           // the service and the settings screen each hold the loop while they're alive
    private static boolean poke;
    private static volatile boolean live, reachable = true;
    private static volatile String lastError = "";

    static boolean reachable() { return reachable; }
    static String lastError() { return lastError; }

    static void hold(Context context) {
        final Context c = context.getApplicationContext();
        synchronized (loop) {
            users++;
            if (thread != null) { loop.notifyAll(); return; }
            thread = new Thread(() -> run(c), "sync");
            thread.setDaemon(true);
            thread.start();
        }
    }

    static void release() {
        synchronized (loop) { users = Math.max(0, users - 1); loop.notifyAll(); }
    }

    // Projectivy keeps the wallpaper service bound while its home screen shows. When it lets go (another app opened),
    // the remote is told "not on the home screen" straight away (Android ends this app soon after, so it can't wait),
    // and the loop stops once that's sent.
    private static boolean serviceHeld, sayAway;
    private static volatile boolean away;

    static boolean away() { return away; }

    static void serviceUp(Context c) {
        boolean held;
        synchronized (loop) {
            away = false;
            sayAway = false;
            held = serviceHeld;
            serviceHeld = true;
            poke = true;
            loop.notifyAll();
        }
        Log.i(ListStore.TAG, "home screen: bound");
        if (!held) hold(c);
    }

    static void serviceDown() {
        synchronized (loop) { away = true; sayAway = true; poke = true; loop.notifyAll(); }
        Log.i(ListStore.TAG, "home screen: unbound");
    }

    /** Check in now (a change to send, a new wallpaper, a command to answer). */
    static void kick(Context c) {
        synchronized (loop) { poke = true; loop.notifyAll(); }
    }

    static void statusChanged(Context c) { kick(c); }

    private static void run(Context c) {
        PowerManager pm = (PowerManager) c.getSystemService(Context.POWER_SERVICE);
        while (true) {
            long wait;
            synchronized (loop) {
                if (users == 0) { thread = null; return; }
                poke = false;
            }
            boolean goingAway;
            synchronized (loop) { goingAway = sayAway; }
            if (paired(c) && (pm == null || pm.isInteractive())) {
                try { checkIn(c); reachable = true; lastError = ""; }
                catch (Exception e) { reachable = false; lastError = e.getMessage(); Log.w(ListStore.TAG, "sync: " + e.getMessage()); }
                wait = live ? 1_500 : reachable ? 10_000 : 60_000;
            } else {
                wait = 60_000;
            }
            synchronized (loop) {
                // "not on the home screen" is sent (or can't be): the service's hold goes, unless it's back meanwhile
                if (goingAway && sayAway && away) { sayAway = false; serviceHeld = false; users = Math.max(0, users - 1); }
            }
            synchronized (loop) {
                long until = System.currentTimeMillis() + wait;
                while (!poke && users > 0) {
                    long left = until - System.currentTimeMillis();
                    if (left <= 0) break;
                    try { loop.wait(left); } catch (InterruptedException e) { return; }
                }
            }
        }
    }

    private static void checkIn(Context c) throws Exception {
        SharedPreferences p = ListStore.prefs(c);
        if (p.getBoolean("settings_dirty", false)) {
            String id = p.getString("sync_id", "");
            JSONObject o = new JSONObject().put("op", "settings").put("device", id).put("patch", toJson(settings(c)));
            call(c, "op", new JSONObject().put("ops", new JSONArray().put(o)), true);
            p.edit().putBoolean("settings_dirty", false).putInt("doc_v", -1).apply();   // read the doc back
        }
        JSONObject body = new JSONObject().put("v", p.getInt("doc_v", -1)).put("cs", p.getInt("cmd_seq", 0));
        JSONObject st = ShuffleService.status(c);
        String hash = Integer.toHexString(st.toString().hashCode());
        boolean sendSt = !hash.equals(p.getString("st_hash", ""));
        if (sendSt) body.put("st", st);
        JSONObject r = call(c, "poll", body, true);
        SharedPreferences.Editor e = p.edit();
        if (sendSt) e.putString("st_hash", hash);
        boolean wasLive = live;
        live = r.optBoolean("live");
        if (live != wasLive) Log.i(ListStore.TAG, live ? "remote open: checking every 1.5 s" : "remote closed: checking every 10 s");
        long now = r.optLong("now", System.currentTimeMillis());
        JSONObject d = r.optJSONObject("doc");
        if (d != null) { saveDoc(c, d); e.putInt("doc_v", r.optInt("v")); }
        e.putInt("cmd_seq", r.optInt("cs", p.getInt("cmd_seq", 0))).apply();
        if (d != null) afterDoc(c);
        JSONArray cmds = r.optJSONArray("cmds");
        if (cmds != null) for (int i = 0; i < cmds.length(); i++) command(c, cmds.optJSONObject(i), now);
    }

    // A new document: take this TV's settings from it (unless a change made here is still on its way up).
    private static void afterDoc(Context c) {
        JSONObject me = doc(c).optJSONObject("devices");
        me = me == null ? null : me.optJSONObject(ListStore.prefs(c).getString("sync_id", ""));
        if (me != null) {
            String name = me.optString("name");
            if (!name.isEmpty()) ListStore.prefs(c).edit().putString("sync_name", name).apply();
            JSONObject s = me.optJSONObject("settings");
            if (s != null && !ListStore.prefs(c).getBoolean("settings_dirty", false)) {
                TvSettings next = fromJson(s).clean();
                if (!toJson(next).toString().equals(toJson(settings(c)).toString())) saveSettings(c, next, false);
            }
        }
        // a playlist this TV shuffles may have changed, or a wallpaper may have been hidden
        ShuffleService.poolMayHaveChanged(c);
        notifyListeners();
    }

    private static void command(Context c, JSONObject cmd, long now) {
        if (cmd == null) return;
        Log.i(ListStore.TAG, cmd.optString("type") + " (remote, sent " + Math.max(0, (now - cmd.optLong("at", now)) / 1000) + " s ago)");
        ListStore.prefs(c).edit().putInt("ack_seq", Math.max(ListStore.prefs(c).getInt("ack_seq", 0), cmd.optInt("seq"))).apply();
        statusChanged(c);   // the remote hears it arrived (and when the change is expected) at once
        switch (cmd.optString("type")) {
            case "next": ShuffleService.skipWait(c); break;
            case "prev": ShuffleService.previous(c); break;
            case "show": { Shuffle.Item w = item(cmd.optJSONObject("item")); if (w != null) ShuffleService.show(c, w); break; }
            case "refresh": ListStore.refreshNow(c, null); break;
            default: break;
        }
    }

    // ---- HTTP

    private static JSONObject call(Context c, String action, JSONObject body, boolean auth) throws Exception {
        String token = auth ? ListStore.prefs(c).getString("sync_token", "") : null;
        if (auth && token.isEmpty()) throw new IllegalStateException("not paired");
        return request(c, action, body, token);
    }

    private static JSONObject request(Context c, String action, JSONObject body, String token) throws Exception {
        String server = ListStore.server(c);
        if (server.isEmpty()) throw new IllegalStateException("Set the service address first");
        HttpURLConnection http = (HttpURLConnection) new URL(server + "/api/sync/" + action).openConnection();
        http.setConnectTimeout(10_000);
        http.setReadTimeout(15_000);
        http.setRequestMethod("POST");
        http.setDoOutput(true);
        http.setRequestProperty("Content-Type", "application/json");
        http.setRequestProperty("User-Agent", "WallhavenShuffle-Projectivy/" + VERSION);
        if (token != null) http.setRequestProperty("Authorization", "Bearer " + token);
        try {
            try (OutputStream out = http.getOutputStream()) { out.write(body.toString().getBytes(StandardCharsets.UTF_8)); }
            int code = http.getResponseCode();
            InputStream in = code < 400 ? http.getInputStream() : http.getErrorStream();
            String text = in == null ? "" : new String(readAll(in), StandardCharsets.UTF_8);
            JSONObject j;
            try { j = new JSONObject(text); } catch (Exception e) { j = new JSONObject(); }
            if (code == 401 && token != null) {
                // signed out from the remote: forget the token, keep working on our own
                ListStore.prefs(c).edit().remove("sync_token").remove("sync_id").putInt("doc_v", -1).apply();
                notifyListeners();
            }
            if (code >= 400) throw new IllegalStateException(j.optString("error", "the service answered " + code));
            return j;
        } finally {
            http.disconnect();
        }
    }

    private static byte[] readAll(InputStream in) throws Exception {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buf = new byte[16 * 1024];
        for (int n; (n = in.read(buf)) > 0; ) out.write(buf, 0, n);
        in.close();
        return out.toByteArray();
    }
}
