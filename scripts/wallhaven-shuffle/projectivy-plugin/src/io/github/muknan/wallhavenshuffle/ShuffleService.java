package io.github.muknan.wallhavenshuffle;

import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.IBinder;
import android.util.Log;

import org.json.JSONObject;

import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.List;

import tv.projectivy.plugin.wallpaperprovider.api.Event;
import tv.projectivy.plugin.wallpaperprovider.api.IWallpaperProviderService;
import tv.projectivy.plugin.wallpaperprovider.api.Wallpaper;

/**
 * What Projectivy talks to. Projectivy keeps what a plugin returns for itemsCacheDurationMillis and, at each change
 * ("Update delay"), shows a random one of those. So this returns exactly one wallpaper, the next in the rotation,
 * and asks Projectivy to keep it for 30 seconds: at every change Projectivy asks again and gets the next one. The
 * result is the Windows app's order on the TV: every wallpaper once per round, no repeats until all were shown.
 */
public final class ShuffleService extends Service {
    static final String PROJECTIVY = "com.spocky.projengmenu";
    static final String PROVIDER_ID = "7987f08d-6228-4006-aa7e-ef456f06bac9";   // the uuid in the manifest
    static final String ACTION_UPDATED = "tv.projectivy.plugin.action.WALLPAPER_PROVIDER_UPDATED";

    private final IWallpaperProviderService.Stub binder = new IWallpaperProviderService.Stub() {
        @Override public List<Wallpaper> getWallpapers(Event event) {
            List<Wallpaper> out = new ArrayList<>();
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

    @Override public IBinder onBind(Intent intent) { return binder; }

    /** The next wallpaper (recorded as shown), or null while there's no list yet. */
    static synchronized Wallpaper next(Context c) {
        ListStore.refreshIfDue(c);                                 // in the background; never holds up this answer
        List<Shuffle.Item> pool = ListStore.pool(c);
        if (pool.isEmpty()) return null;
        SharedPreferences p = ListStore.prefs(c);
        Shuffle s = new Shuffle(Shuffle.split(p.getString("round", "")), Shuffle.split(p.getString("history", "")), new SecureRandom());
        Shuffle.Item w = s.next(pool);
        p.edit().putString("round", s.roundState()).putString("history", s.historyState())
            .putString("current", w.id).putInt("shown", s.shownThisRound(pool)).apply();
        Log.i(ListStore.TAG, "wallpaper " + w.id + " - " + s.shownThisRound(pool) + " of " + pool.size());
        return new Wallpaper(w.image, Wallpaper.TYPE_IMAGE, Wallpaper.MODE_CROP, null, w.page, "Wallhaven", null);
    }

    /** Tells Projectivy to ask again now (settings changed, or "next wallpaper"). Projectivy acts on "settings changed"
     *  (reason 1) right away, but on "data changed" (2) only at its next change, so Next uses reason 1 as well. */
    static void askProjectivyToRefresh(Context c, int reason) {
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

    // Projectivy's settings backup includes each plugin's settings, as a string.
    static String exportPrefs(Context c) {
        SharedPreferences p = ListStore.prefs(c);
        try {
            return new JSONObject().put("list_url", p.getString("list_url", "")).put("max_width", p.getInt("max_width", 3840))
                .put("min_favorites", p.getInt("min_favorites", 0)).toString();
        } catch (Exception e) { return "{}"; }
    }

    static void importPrefs(Context c, String json) {
        try {
            JSONObject o = new JSONObject(json);
            ListStore.prefs(c).edit().putString("list_url", o.optString("list_url", "")).putInt("max_width", o.optInt("max_width", 3840))
                .putInt("min_favorites", o.optInt("min_favorites", 0)).putLong("list_time", 0).apply();
        } catch (Exception e) { Log.w(ListStore.TAG, "couldn't import settings: " + e); }
    }
}
