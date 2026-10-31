package io.github.muknan.wallhavenshuffle;

import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.IBinder;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

import tv.projectivy.plugin.wallpaperprovider.api.Event;
import tv.projectivy.plugin.wallpaperprovider.api.IWallpaperProviderService;
import tv.projectivy.plugin.wallpaperprovider.api.Wallpaper;

/**
 * What Projectivy talks to. Projectivy keeps what a plugin returns for itemsCacheDurationMillis and, at each change
 * ("Update delay"), shows a random one of those. So this returns exactly one wallpaper, the next in the rotation,
 * and asks Projectivy to keep it for 1 second: at every change Projectivy asks again and gets the next one, and a
 * refresh asked for at any moment is answered at once (it ignores one while what it has is fresh: with 30 s, as up to
 * 1.12, a Next just after a change waited up to 30 s; Projectivy reads this when it starts). The
 * result is the Windows app's order on the TV: every wallpaper once per round, no repeats until all were shown.
 *
 * It answers with nothing (Projectivy keeps the wallpaper it has) while paused or keeping one, and until this TV's
 * own "Change" interval has passed; Next, Previous and "Show on TV" from the remote skip that wait.
 */
public final class ShuffleService extends Service {
    static final String PROJECTIVY = "com.spocky.projengmenu";
    static final String PROVIDER_ID = "7987f08d-6228-4006-aa7e-ef456f06bac9";   // the uuid in the manifest
    static final String ACTION_UPDATED = "tv.projectivy.plugin.action.WALLPAPER_PROVIDER_UPDATED";
    static final int RECENT = 20;

    private final IWallpaperProviderService.Stub binder = new IWallpaperProviderService.Stub() {
        @Override public List<Wallpaper> getWallpapers(Event event) {
            List<Wallpaper> out = new ArrayList<>();
            Log.d(ListStore.TAG, "asked by Projectivy (event " + (event == null ? "none" : event.eventType) + ")");
            Sync.serviceUp(ShuffleService.this);   // Projectivy asking: its home screen is showing
            if (event == null || event.eventType != Event.TIME_ELAPSED) return out;   // empty = keep the current one
            try {
                Wallpaper w = next(ShuffleService.this);
                if (w != null) out.add(w);
            } catch (Exception e) {
                Log.w(ListStore.TAG, "next wallpaper failed: " + e);  // never crash Projectivy's call
            }
            return out;
        }

        @Override public String getPreferences() { return exportPrefs(ShuffleService.this); }
        @Override public void setPreferences(String params) { importPrefs(ShuffleService.this, params); }
    };

    private static volatile long boundAt, refreshAskedAt;
    static long base(Context c) { return Shuffle.base(Shuffle.split(ListStore.prefs(c).getString("gaps", ""))); }

    @Override public void onCreate() { super.onCreate(); boundAt = System.currentTimeMillis(); Sync.serviceUp(this); }
    @Override public void onDestroy() { Sync.serviceDown(); super.onDestroy(); }
    @Override public IBinder onBind(Intent intent) { return binder; }

    // ---- what to show

    /** The wallpapers this TV shuffles now: its sources mixed, Hidden left out, the width limit on the toplist. */
    static List<Shuffle.Item> pool(Context c, TvSettings s) {
        Set<String> hidden = Sync.hiddenIds(c);
        Map<String, Shuffle.Item> out = new LinkedHashMap<>();
        for (String src : s.sources) {
            if (src.equals("toplist")) {
                for (Shuffle.Item w : ListStore.pool(c))
                    if ((s.maxw == 0 || w.width <= s.maxw) && !hidden.contains(w.id)) out.put(w.id, w);
            } else {
                for (Shuffle.Item w : Sync.items(c, src)) if (!hidden.contains(w.id)) out.put(w.id, w);
            }
        }
        return new ArrayList<>(out.values());
    }

    /** The next wallpaper (recorded as shown), or null to keep the one on screen. */
    static synchronized Wallpaper next(Context c) {
        SharedPreferences p = ListStore.prefs(c);
        long now = System.currentTimeMillis();
        // How often Projectivy asks on its own (its Update delay), to say when the next change comes: only gaps that
        // end in one of its own asks count (not one this plugin asked for, nor the first after it binds, which may come
        // after a long time in another app), the last five of them.
        long lastAsk = p.getLong("asked_at", 0);
        boolean own = now - refreshAskedAt > 5_000 && now - boundAt > 5_000;
        SharedPreferences.Editor ed = p.edit().putLong("asked_at", now).remove("base_ms");
        if (own && lastAsk > 0 && now - lastAsk > 20_000 && now - lastAsk < 3 * 3600_000L) {
            List<String> gaps = Shuffle.split(p.getString("gaps", ""));
            gaps.add(String.valueOf(now - lastAsk));
            while (gaps.size() > 5) gaps.remove(0);
            ed.putString("gaps", String.join(",", gaps));
        }
        ed.apply();

        ListStore.refreshIfDue(c);                                 // in the background; never holds up this answer
        TvSettings s = Sync.settings(c);
        boolean forced = p.getBoolean("skip_wait", false);
        String pending = p.getString("pending_json", "");
        if (!pending.isEmpty()) {                                  // Previous, or "Show on TV" from the remote
            p.edit().remove("pending_json").putBoolean("skip_wait", false).apply();
            try {
                Shuffle.Item w = Sync.item(new JSONObject(pending));
                if (w != null) return shown(c, w, now, true);
            } catch (Exception ignored) { }
        }
        if (!forced) {
            if (s.paused || !s.keep.isEmpty()) return null;
            long changed = p.getLong("changed_at", 0);
            if (now - changed < s.every * 60_000L - 15_000) return null;   // 15 s early is fine: Projectivy's timer isn't exact
        }
        List<Shuffle.Item> pool = pool(c, s);
        if (pool.isEmpty()) { p.edit().putBoolean("skip_wait", false).apply(); return null; }
        Shuffle sh = new Shuffle(Shuffle.split(p.getString("round", "")), Shuffle.split(p.getString("history", "")), new java.util.Random(seed(p)));
        Shuffle.Item w = sh.next(pool);
        p.edit().putString("round", sh.roundState()).putString("history", sh.historyState()).putInt("shown", sh.shownThisRound(pool))
            .putInt("total", pool.size()).putBoolean("skip_wait", false).putLong("seed", new SecureRandom().nextLong() | 1).apply();
        Log.i(ListStore.TAG, "wallpaper " + w.id + " - " + sh.shownThisRound(pool) + " of " + pool.size());
        return shown(c, w, now, true);
    }

    // Records what's on screen now and answers Projectivy with it.
    private static Wallpaper shown(Context c, Shuffle.Item w, long now, boolean fresh) {
        SharedPreferences p = ListStore.prefs(c);
        JSONArray recent = new JSONArray();
        try {
            String cur = p.getString("current_json", "");
            if (fresh && !cur.isEmpty()) recent.put(new JSONObject(cur));
            JSONArray old = new JSONArray(p.getString("recent_json", "[]"));
            for (int i = 0; i < old.length() && recent.length() < RECENT; i++) if (!w.id.equals(old.optJSONObject(i).optString("id"))) recent.put(old.get(i));
        } catch (Exception ignored) { }
        p.edit().putString("current", w.id).putString("current_json", Sync.itemJson(w).toString()).putString("recent_json", recent.toString())
            .putLong("changed_at", now).remove("change_eta").apply();
        Sync.statusChanged(c);
        return new Wallpaper(w.image, Wallpaper.TYPE_IMAGE, Wallpaper.MODE_CROP, null, w.page, "Wallhaven", null);
    }

    // ---- what the remote (and the settings screen) can ask for

    /** Next, right away. */
    static void skipWait(Context c) {
        ListStore.prefs(c).edit().putBoolean("skip_wait", true).apply();
        askUntilChanged(c);
    }

    // A change asked for from the remote: Projectivy answers a refresh at once (see the class comment), so it's
    // expected in a second or two (within a few seconds of a change it waits a little). Until the wallpaper has actually
    // changed it's asked for again, at 2, 4, 7, 12 and 31 s
    // (31 s covers a Projectivy that still has the old 30 s from before it restarted), and the remote is told when
    // to expect it ("Changing…", or "Changing in 0:20" after an ask was ignored).
    private static final long[] RETRY_AT = { 2_000, 4_000, 7_000, 12_000, 31_000 };
    private static final android.os.Handler main = new android.os.Handler(android.os.Looper.getMainLooper());
    private static final Object RETRY = new Object();
    private static long wantedAt, nextAsk;
    private static int asks;

    private static void askUntilChanged(Context c) {
        final Context app = c.getApplicationContext();
        SharedPreferences p = ListStore.prefs(app);
        long now = System.currentTimeMillis();
        main.removeCallbacksAndMessages(RETRY);
        wantedAt = now;
        asks = 0;
        askProjectivyToRefresh(app, 1);
        nextAsk = now + RETRY_AT[0];
        p.edit().putLong("change_eta", now + 1_500).apply();
        Sync.statusChanged(app);
        tick(app);
    }

    private static void tick(Context app) {
        main.postAtTime(() -> {
            SharedPreferences p = ListStore.prefs(app);
            boolean waiting = p.getBoolean("skip_wait", false) || !p.getString("pending_json", "").isEmpty();
            if (!waiting || p.getLong("changed_at", 0) >= wantedAt) return;
            long now = System.currentTimeMillis(), eta = p.getLong("change_eta", 0);
            if (now >= nextAsk) {
                if (asks >= RETRY_AT.length) { p.edit().remove("change_eta").apply(); Sync.statusChanged(app); return; }   // Projectivy isn't listening
                asks++;
                Log.i(ListStore.TAG, "still waiting for Projectivy: asking again (" + asks + ")");
                askProjectivyToRefresh(app, 1);
                nextAsk = asks < RETRY_AT.length ? wantedAt + RETRY_AT[asks] : now + 3_000;
                p.edit().putLong("change_eta", now + 1_500).apply();
                Sync.statusChanged(app);
            } else if (eta > 0 && eta + 1_000 < now && eta < nextAsk) {
                p.edit().putLong("change_eta", nextAsk + 1_000).apply();   // that ask was ignored: the next one, then
                Sync.statusChanged(app);
            }
            tick(app);
        }, RETRY, android.os.SystemClock.uptimeMillis() + 1_000);
    }

    /** What Next would show: the same pick next() makes, as long as nothing changes meanwhile (the remote shows it
     *  straight away when Next is pressed). */
    static Shuffle.Item upcoming(Context c, List<Shuffle.Item> pool) {
        SharedPreferences p = ListStore.prefs(c);
        try { String pending = p.getString("pending_json", ""); if (!pending.isEmpty()) return Sync.item(new JSONObject(pending)); } catch (Exception ignored) { }
        if (pool.isEmpty()) return null;
        return new Shuffle(Shuffle.split(p.getString("round", "")), Shuffle.split(p.getString("history", "")), new java.util.Random(seed(p))).next(pool);
    }

    // The pick's random seed: the same for upcoming() and next() until a wallpaper is picked, then a new one.
    private static long seed(SharedPreferences p) {
        long seed = p.getLong("seed", 0);
        if (seed == 0) { seed = new SecureRandom().nextLong() | 1; p.edit().putLong("seed", seed).apply(); }
        return seed;
    }

    /** Back to the one before (out of this round again, as on the PC). */
    static synchronized void previous(Context c) {
        SharedPreferences p = ListStore.prefs(c);
        try {
            JSONArray recent = new JSONArray(p.getString("recent_json", "[]"));
            if (recent.length() == 0) return;
            JSONObject prev = recent.getJSONObject(0);
            JSONArray rest = new JSONArray();
            for (int i = 1; i < recent.length(); i++) rest.put(recent.get(i));
            String cur = p.getString("current", "");
            List<String> round = Shuffle.split(p.getString("round", "")), history = Shuffle.split(p.getString("history", ""));
            round.remove(cur);
            history.remove(cur);
            p.edit().putString("round", String.join(",", round)).putString("history", String.join(",", history))
                .putInt("shown", Math.max(0, p.getInt("shown", 1) - 1)).putString("recent_json", rest.toString())
                .putString("current_json", "").putString("pending_json", prev.toString()).apply();
        } catch (Exception e) { Log.w(ListStore.TAG, "previous failed: " + e); return; }
        askUntilChanged(c);
    }

    /** This wallpaper, now ("Show on TV" from the remote or the PC). */
    static void show(Context c, Shuffle.Item w) {
        ListStore.prefs(c).edit().putString("pending_json", Sync.itemJson(w).toString()).apply();
        askUntilChanged(c);
    }

    /** The playlists or the toplist changed: if what's on screen was hidden meanwhile, move on. */
    static void poolMayHaveChanged(Context c) {
        String cur = ListStore.prefs(c).getString("current", "");
        if (!cur.isEmpty() && Sync.hiddenIds(c).contains(cur)) skipWait(c);
    }

    /** The current wallpaper, as the remote shows it ({id, ext, res, fav}), or null. */
    static Shuffle.Item current(Context c) {
        SharedPreferences p = ListStore.prefs(c);
        try { return Sync.item(new JSONObject(p.getString("current_json", ""))); } catch (Exception ignored) { }
        // shown by 1.7, which kept only its id: its details from the saved list
        String id = p.getString("current", "");
        if (id.isEmpty()) return null;
        for (Shuffle.Item w : ListStore.pool(c)) if (w.id.equals(id)) return w;
        return null;
    }

    /** What the remote shows for this TV. */
    static JSONObject status(Context c) {
        SharedPreferences p = ListStore.prefs(c);
        TvSettings s = Sync.settings(c);
        JSONObject st = new JSONObject();
        try {
            st.put("app", "tv " + Sync.VERSION);
            if (Sync.away()) st.put("away", true);
            Shuffle.Item cur = current(c);
            if (cur != null) st.put("cur", Sync.itemJson(cur).put("key", cur.id));
            st.put("shown", p.getInt("shown", 0)).put("total", p.getInt("total", 0));
            long base = base(c), changed = p.getLong("changed_at", 0), lastAsk = p.getLong("asked_at", 0);
            if (!s.paused && s.keep.isEmpty() && changed > 0 && lastAsk > 0)
                st.put("next", Shuffle.nextChange(lastAsk, base, changed, s.every * 60_000L, System.currentTimeMillis()));
            // the settings as this TV has them (the remote shows "Updating…" until they match what it asked for)
            st.put("paused", s.paused).put("kept", !s.keep.isEmpty()).put("every", s.every);
            st.put("recent", new JSONArray(p.getString("recent_json", "[]")));
            List<Shuffle.Item> pool = pool(c, s);
            Shuffle.Item up = upcoming(c, pool);
            if (up != null) st.put("up", Sync.itemJson(up));
            // the last command from the remote it has seen, and when the change it asked for is expected
            st.put("ack", new JSONObject().put("seq", p.getInt("ack_seq", 0)).put("eta", p.getLong("change_eta", 0)));
            int toplist = 0;
            for (Shuffle.Item w : ListStore.pool(c)) if (s.maxw == 0 || w.width <= s.maxw) toplist++;
            st.put("counts", new JSONObject().put("toplist", toplist));
            st.put("list", p.getString("list_for", ""));
            st.put("base", base / 1000);
            if (s.sources.isEmpty()) st.put("note", "Nothing to shuffle: turn something on in Shuffle from");
            else if (pool.isEmpty()) st.put("note", s.sources.contains("toplist") ? "Getting the list…" : "Those playlists are empty");
        } catch (Exception e) { Log.w(ListStore.TAG, "status: " + e); }
        return st;
    }

    /** Tells Projectivy to ask again now (settings changed, or "next wallpaper"). Projectivy acts on "settings changed"
     *  (reason 1) right away, but on "data changed" (2) only at its next change, so Next uses reason 1 as well. */
    static void askProjectivyToRefresh(Context c, int reason) {
        Log.d(ListStore.TAG, "asking Projectivy to refresh (" + reason + ")");
        refreshAskedAt = System.currentTimeMillis();
        Intent i = new Intent(ACTION_UPDATED)
            .putExtra("tv.projectivy.plugin.extra.PROVIDER_ID", PROVIDER_ID)
            .putExtra("tv.projectivy.plugin.extra.UPDATE_REASON", reason)
            .setPackage(PROJECTIVY);
        // Addressed to Projectivy's receiver by name, found at run time: since Android 8 a broadcast that only names
        // the package doesn't reach a receiver declared in an app's manifest.
        List<android.content.pm.ResolveInfo> receivers = c.getPackageManager().queryBroadcastReceivers(i, 0);
        if (receivers.isEmpty()) { c.sendBroadcast(i); return; }
        for (android.content.pm.ResolveInfo r : receivers)
            c.sendBroadcast(new Intent(i).setComponent(new android.content.ComponentName(r.activityInfo.packageName, r.activityInfo.name)));
    }

    // Projectivy's settings backup includes each plugin's settings, as a string (not the sync sign-in).
    static String exportPrefs(Context c) {
        try {
            return new JSONObject().put("server", ListStore.server(c)).put("settings", Sync.toJson(Sync.settings(c))).toString();
        } catch (Exception e) { return "{}"; }
    }

    static void importPrefs(Context c, String json) {
        try {
            JSONObject o = new JSONObject(json);
            String server = o.optString("server", "");
            if (server.isEmpty() && !o.optString("list_url", "").isEmpty()) ListStore.prefs(c).edit().putString("list_url", o.optString("list_url")).apply();   // a 1.x backup
            else if (!server.isEmpty()) ListStore.prefs(c).edit().putString("server", server).apply();
            JSONObject s = o.optJSONObject("settings");
            if (s != null) Sync.saveSettings(c, Sync.fromJson(s), true);
            ListStore.prefs(c).edit().putLong("list_time", 0).apply();
        } catch (Exception e) { Log.w(ListStore.TAG, "couldn't import settings: " + e); }
    }
}
