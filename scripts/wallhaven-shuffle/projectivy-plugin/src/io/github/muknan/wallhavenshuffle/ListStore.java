package io.github.muknan.wallhavenshuffle;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.io.Writer;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.function.Function;

/**
 * The lists, kept on the TV: the toplist, and Most favourited (all of Wallhaven by favourites, the part in its
 * range). Each is downloaded from the wallhaven-shuffle service's /api/list for this TV's settings (with no time
 * limit to speak of: the service may need a couple of minutes to put a cold list together) and saved as a compact
 * file of just what this TV shows (pool.tsv, mostfav.tsv; ~100 KB), so a request reads a small file in milliseconds
 * instead of waiting on the network or parsing 1.5 MB of JSON every minute. An old copy keeps working while a new
 * one is fetched, and if the service can't be reached the old one simply stays.
 */
final class ListStore {
    static final String TAG = "WallhavenShuffle";
    static final String PREFS = "settings";
    static final long REFRESH_AFTER_MS = 12L * 3600 * 1000;      // a new list twice a day is plenty (toplists change slowly)
    static final long RETRY_AFTER_MS = 10L * 60 * 1000;          // after a failed download
    static final long HUNG_AFTER_MS = 6L * 60 * 1000;            // a download running longer than this no longer blocks new ones

    /** One list: its file, its keys in the settings (prefix_time, _tried, _for, _size), and the query it's for. */
    static final class Kind {
        final String file, key, name;
        final Function<TvSettings, String> query;
        final boolean emptyOk;                                     // Most favourited: a range with nothing in it is an answer
        private List<Shuffle.Item> cached = Collections.emptyList();
        private long cachedStamp = -1;
        // One download at a time. A request that comes while one runs (the settings were just changed) isn't
        // dropped: the running one goes round again with the current settings when it's done. One running for over
        // HUNG_AFTER_MS (a hung connection) no longer blocks new ones, and its result is thrown away.
        private final Object gate = new Object();
        private long running;                                      // when the running download started; 0 = none
        private boolean again;
        private int generation;
        Kind(String file, String key, String name, Function<TvSettings, String> query, boolean emptyOk) {
            this.file = file; this.key = key; this.name = name; this.query = query; this.emptyOk = emptyOk;
        }
        String forKey() { return key + "_for"; }
    }
    // (the toplist keeps the keys it always had: list_time, list_tried, list_for, list_size)
    static final Kind TOPLIST = new Kind("pool.tsv", "list", "the list", TvSettings::listQuery, false);
    static final Kind MOSTFAV = new Kind("mostfav.tsv", "fav", "Most favourited", TvSettings::mostfavQuery, true);

    static SharedPreferences prefs(Context c) { return c.getSharedPreferences(PREFS, Context.MODE_PRIVATE); }
    static File poolFile(Context c) { return new File(c.getFilesDir(), TOPLIST.file); }

    /** The service's address (https://<project>.vercel.app): set on its own, or the start of the old list address. */
    static String server(Context c) {
        SharedPreferences p = prefs(c);
        String s = p.getString("server", "");
        if (s.isEmpty()) {
            android.net.Uri u = android.net.Uri.parse(p.getString("list_url", ""));
            if (u.getScheme() != null && u.getHost() != null) s = u.getScheme() + "://" + u.getHost() + (u.getPort() > 0 ? ":" + u.getPort() : "");
        }
        return s.replaceAll("/+$", "");
    }

    /** The toplist this TV has, every width (the width limit is applied when the pool is put together). */
    static List<Shuffle.Item> pool(Context c) { return pool(c, TOPLIST); }
    /** Most favourited's list as last fetched (its range is applied again when the pool is put together). */
    static List<Shuffle.Item> favPool(Context c) { return pool(c, MOSTFAV); }

    static List<Shuffle.Item> pool(Context c, Kind k) {
        synchronized (k) {
            File f = new File(c.getFilesDir(), k.file);
            long stamp = f.exists() ? f.lastModified() * 31 + f.length() + 1 : 0;   // time and size: a new file is never missed
            if (stamp == k.cachedStamp) return k.cached;
            List<Shuffle.Item> items = new ArrayList<>();
            if (stamp != 0) {
                try (BufferedReader r = new BufferedReader(new InputStreamReader(new FileInputStream(f), StandardCharsets.UTF_8))) {
                    for (String line; (line = r.readLine()) != null; ) {
                        String[] p = line.split("\t", -1);
                        if (p.length >= 6) items.add(new Shuffle.Item(p[0], p[1], p[2], p[3], toInt(p[4]), toInt(p[5])));
                    }
                } catch (Exception e) {
                    Log.w(TAG, "couldn't read the saved list: " + e);
                }
            }
            k.cached = Collections.unmodifiableList(items);
            k.cachedStamp = stamp;
            return k.cached;
        }
    }

    /** Starts downloads in the background for the lists that are old enough (or missing, or for other settings):
     *  the toplist always (playlists use its details), Most favourited while it's on. Never blocks. */
    static void refreshIfDue(final Context context) {
        final Context c = context.getApplicationContext();
        TvSettings s = Sync.settings(c);
        refreshIfDue(c, TOPLIST, s);
        if (s.sources.contains("mostfav")) refreshIfDue(c, MOSTFAV, s);
    }

    private static void refreshIfDue(Context c, Kind k, TvSettings s) {
        SharedPreferences p = prefs(c);
        long now = System.currentTimeMillis();
        long last = p.getLong(k.key + "_time", 0), tried = p.getLong(k.key + "_tried", 0);
        boolean due = !new File(c.getFilesDir(), k.file).exists() || now - last > REFRESH_AFTER_MS || !p.getString(k.forKey(), "").equals(k.query.apply(s));
        if (!due || now - tried < RETRY_AFTER_MS || server(c).isEmpty()) return;
        refreshNow(c, k, null);
    }

    interface Done { void done(String result); }

    /** Downloads the toplist now, in the background; done (if any) gets a one-line result. */
    static void refreshNow(final Context context, final Done done) { refreshNow(context, TOPLIST, done); }

    static void refreshNow(final Context context, final Kind k, final Done done) {
        final Context c = context.getApplicationContext();
        final int gen;
        synchronized (k.gate) {
            long now = System.currentTimeMillis();
            if (k.running != 0 && now - k.running < HUNG_AFTER_MS) { k.again = true; return; }
            k.running = now; k.again = false; gen = ++k.generation;
        }
        prefs(c).edit().putLong(k.key + "_tried", System.currentTimeMillis()).apply();
        new Thread(() -> {
            String result;
            while (true) {
                try { result = download(c, k, gen); }
                catch (Exception e) { result = "couldn't get " + k.name + ": " + e.getMessage(); Log.w(TAG, result); }
                synchronized (k.gate) {
                    if (gen != k.generation) return;               // a newer download took over
                    if (!k.again) { k.running = 0; break; }
                    k.again = false; k.running = System.currentTimeMillis();
                }
            }
            if (done != null) done.done(result);
        }, k.key + "-download").start();
    }

    private static String download(Context c, Kind k, int gen) throws Exception {
        SharedPreferences p = prefs(c);
        String query = k.query.apply(Sync.settings(c)), server = server(c);   // read now: the latest settings
        if (server.isEmpty()) throw new IllegalStateException("no service address set");
        String url = server + "/api/list?" + query;
        HttpURLConnection http = (HttpURLConnection) new URL(url).openConnection();
        http.setConnectTimeout(30_000);
        http.setReadTimeout(300_000);                             // a cold list can take the service a few minutes
        http.setRequestProperty("User-Agent", "WallhavenShuffle-Projectivy/" + Sync.VERSION);
        byte[] body;
        try {
            int code = http.getResponseCode();                     // first: a 404's own error only names the address
            if (code != 200) throw new IllegalStateException("the service answered " + code + " (is the address right?)");
            try (InputStream in = http.getInputStream()) { body = readAll(in); }
        } finally {
            http.disconnect();
        }

        JSONObject root;
        JSONArray arr;
        try {
            root = new JSONObject(new String(body, StandardCharsets.UTF_8));
            arr = root.has("items") ? root.getJSONArray("items") : root.getJSONArray("data");
        } catch (JSONException e) {
            throw new IllegalStateException("that address doesn't give a wallpaper list (use the service's /api/list link)");
        }
        boolean complete = root.optBoolean("complete", true);
        // Most favourited: when its range holds more than one list takes, about how many it holds
        int of = root.optBoolean("capped", false) ? root.optInt("in_range", 0) : 0;
        File tmp = new File(c.getFilesDir(), k.file + ".new");
        int kept = 0;
        try (Writer w = new OutputStreamWriter(new FileOutputStream(tmp), StandardCharsets.UTF_8)) {
            for (int i = 0; i < arr.length(); i++) {
                JSONObject o = arr.optJSONObject(i);
                if (o == null) continue;
                String id = o.optString("id"), image = o.optString("path");
                int width = o.optInt("dimension_x"), fav = o.optInt("favorites");
                if (!id.matches("[A-Za-z0-9_-]{1,40}") || !image.startsWith("http")) continue;
                w.write(id + "\t" + clean(image) + "\t" + clean(o.optString("url")) + "\t" + clean(o.optString("resolution")) + "\t" + width + "\t" + fav + "\n");
                kept++;
            }
        }
        if (kept == 0 && !(k.emptyOk && complete)) { tmp.delete(); throw new IllegalStateException("no wallpapers match these filters"); }

        // The service marks a list it couldn't read in full (Wallhaven's rate limit) as incomplete; it's filled in a
        // few minutes later. Such a list never replaces a fuller saved one (another try comes after RETRY_AFTER_MS).
        int saved = pool(c, k).size();
        boolean sameList = query.equals(p.getString(k.forKey(), ""));
        if (!complete && kept < saved && sameList) {
            tmp.delete();
            String r = k.name + " partial (" + kept + "), kept the saved one (" + saved + ")";
            Log.i(TAG, r);
            return r;
        }
        synchronized (k.gate) {
            if (gen != k.generation || !query.equals(k.query.apply(Sync.settings(c)))) {   // the settings changed meanwhile
                tmp.delete();
                return "settings changed: getting " + k.name + " again";
            }
            File f = new File(c.getFilesDir(), k.file);
            if (!tmp.renameTo(f)) { f.delete(); if (!tmp.renameTo(f)) throw new IllegalStateException("couldn't save the list"); }
        }
        // A partial list (when nothing fuller was saved) is used, but not counted as fresh: the next try fills it in.
        p.edit().putLong(k.key + "_time", complete ? System.currentTimeMillis() : 0).putInt(k.key + "_size", kept)
            .putInt(k.key + "_of", of).putString(k.forKey(), query).apply();
        ShuffleService.poolMayHaveChanged(c);
        Sync.statusChanged(c);
        String result = (k == TOPLIST ? "list" : k.name) + ": " + kept + " wallpapers" + (of > kept ? " (the most favourited of about " + of + ")" : "")
            + (complete ? "" : " (partial, filled in at the next try)");
        Log.i(TAG, result);
        return result;
    }

    private static String clean(String s) { return s == null ? "" : s.replace('\t', ' ').replace('\n', ' '); }
    private static int toInt(String s) { try { return Integer.parseInt(s); } catch (NumberFormatException e) { return 0; } }

    private static byte[] readAll(InputStream in) throws Exception {
        ByteArrayOutputStream out = new ByteArrayOutputStream(1 << 20);
        byte[] buf = new byte[64 * 1024];
        for (int n; (n = in.read(buf)) > 0; ) out.write(buf, 0, n);
        return out.toByteArray();
    }
}
