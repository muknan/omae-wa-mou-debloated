package io.github.muknan.wallhavenshuffle;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.Log;

import org.json.JSONArray;
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
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * The wallpaper list, kept on the TV. It's downloaded from the wallhaven-shuffle service's /api/list (with no time
 * limit to speak of: the service may need a couple of minutes to put a cold list together) and saved as a compact
 * file of just what this TV shows (pool.tsv, ~100 KB), so a request reads a small file in milliseconds instead of
 * waiting on the network or parsing 1.5 MB of JSON every minute. An old copy keeps working while a new one is
 * fetched, and if the service can't be reached the old one simply stays.
 */
final class ListStore {
    static final String TAG = "WallhavenShuffle";
    static final String PREFS = "settings";
    static final long REFRESH_AFTER_MS = 12L * 3600 * 1000;      // a new list twice a day is plenty (toplists change slowly)
    static final long RETRY_AFTER_MS = 10L * 60 * 1000;          // after a failed download

    private static final AtomicBoolean downloading = new AtomicBoolean();
    private static List<Shuffle.Item> cached = Collections.emptyList();
    private static long cachedStamp = -1;

    static SharedPreferences prefs(Context c) { return c.getSharedPreferences(PREFS, Context.MODE_PRIVATE); }
    static File poolFile(Context c) { return new File(c.getFilesDir(), "pool.tsv"); }

    /** The wallpapers this TV shows, from the saved list (empty until the first download). */
    static synchronized List<Shuffle.Item> pool(Context c) {
        File f = poolFile(c);
        long stamp = f.exists() ? f.lastModified() : 0;
        if (stamp == cachedStamp) return cached;
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
        cached = Collections.unmodifiableList(items);
        cachedStamp = stamp;
        return cached;
    }

    /** Starts a download in the background if the saved list is old enough (or missing). Never blocks. */
    static void refreshIfDue(final Context context) {
        final Context c = context.getApplicationContext();
        SharedPreferences p = prefs(c);
        long now = System.currentTimeMillis();
        long last = p.getLong("list_time", 0), tried = p.getLong("list_tried", 0);
        boolean due = !poolFile(c).exists() || now - last > REFRESH_AFTER_MS;
        if (!due || now - tried < RETRY_AFTER_MS || p.getString("list_url", "").isEmpty()) return;
        refreshNow(c, null);
    }

    interface Done { void done(String result); }

    /** Downloads the list now, in the background; done (if any) gets a one-line result. */
    static void refreshNow(final Context context, final Done done) {
        final Context c = context.getApplicationContext();
        if (!downloading.compareAndSet(false, true)) { if (done != null) done.done("already downloading"); return; }
        prefs(c).edit().putLong("list_tried", System.currentTimeMillis()).apply();
        new Thread(() -> {
            String result;
            try { result = download(c); }
            catch (Exception e) { result = "couldn't get the list: " + e.getMessage(); Log.w(TAG, result); }
            finally { downloading.set(false); }
            if (done != null) done.done(result);
        }, "list-download").start();
    }

    private static String download(Context c) throws Exception {
        SharedPreferences p = prefs(c);
        String url = p.getString("list_url", "");
        if (url.isEmpty()) throw new IllegalStateException("no list address set");
        int maxWidth = p.getInt("max_width", 3840), minFav = p.getInt("min_favorites", 0);
        HttpURLConnection http = (HttpURLConnection) new URL(url).openConnection();
        http.setConnectTimeout(30_000);
        http.setReadTimeout(300_000);                             // a cold list can take the service a few minutes
        http.setRequestProperty("User-Agent", "WallhavenShuffle-Projectivy/1.0");
        byte[] body;
        try (InputStream in = http.getInputStream()) { body = readAll(in); }
        finally { http.disconnect(); }
        if (http.getResponseCode() != 200) throw new IllegalStateException("the service answered " + http.getResponseCode());

        JSONObject root = new JSONObject(new String(body, StandardCharsets.UTF_8));
        JSONArray arr = root.has("items") ? root.getJSONArray("items") : root.getJSONArray("data");
        File tmp = new File(c.getFilesDir(), "pool.tsv.new");
        int kept = 0;
        try (Writer w = new OutputStreamWriter(new FileOutputStream(tmp), StandardCharsets.UTF_8)) {
            for (int i = 0; i < arr.length(); i++) {
                JSONObject o = arr.optJSONObject(i);
                if (o == null) continue;
                String id = o.optString("id"), image = o.optString("path");
                int width = o.optInt("dimension_x"), fav = o.optInt("favorites");
                if (!id.matches("[A-Za-z0-9_-]{1,40}") || !image.startsWith("http")) continue;
                if ((maxWidth > 0 && width > maxWidth) || fav < minFav) continue;
                w.write(id + "\t" + clean(image) + "\t" + clean(o.optString("url")) + "\t" + clean(o.optString("resolution")) + "\t" + width + "\t" + fav + "\n");
                kept++;
            }
        }
        if (kept == 0) { tmp.delete(); throw new IllegalStateException("the list had no wallpapers that fit"); }
        File f = poolFile(c);
        if (!tmp.renameTo(f)) { f.delete(); if (!tmp.renameTo(f)) throw new IllegalStateException("couldn't save the list"); }
        p.edit().putLong("list_time", System.currentTimeMillis()).putInt("list_size", kept)
            .putBoolean("list_complete", root.optBoolean("complete", true)).apply();
        String result = "list: " + kept + " wallpapers" + (root.optBoolean("complete", true) ? "" : " (partial)");
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
