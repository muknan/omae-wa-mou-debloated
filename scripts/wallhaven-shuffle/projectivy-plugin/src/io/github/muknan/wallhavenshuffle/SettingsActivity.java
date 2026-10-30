package io.github.muknan.wallhavenshuffle;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import android.graphics.Outline;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.StateListDrawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.ViewOutlineProvider;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/**
 * Settings, opened from Projectivy (the plugin's "Configure") or the app list: the wallpaper on screen with its
 * actions on the left, and on the right a short list of rows that say their current value and open a picker. Plain
 * views built in code (no libraries), sized for the sofa and made for the remote's arrows and OK button.
 * For setting it up over ADB:
 *   am start -n io.github.muknan.wallhavenshuffle/.SettingsActivity --es server "https://<project>.vercel.app"
 */
public final class SettingsActivity extends Activity implements Sync.Listener {
    // the remote's palette (graphite, a touch cool); the accent only marks what's on
    static final int BG = Color.rgb(14, 15, 17), RAISED = Color.rgb(25, 26, 30), FOCUS = Color.rgb(46, 48, 54);
    static final int FG = Color.rgb(239, 240, 242), FG2 = Color.rgb(162, 165, 173), FG3 = Color.rgb(107, 110, 118);
    static final int ACCENT = Color.rgb(179, 176, 255), HEART = Color.rgb(255, 111, 142), WARN = Color.rgb(241, 180, 91);

    private final Handler ui = new Handler(Looper.getMainLooper());
    private ImageView picture;
    private TextView pictureNote, captionLeft, captionRight, status, title;
    private TextView keepBtn, pauseBtn, favBtn;
    private final List<Runnable> refreshers = new ArrayList<>();
    private String pictureId = "";
    private AlertDialog pairDialog;

    @Override protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        if (saved == null) applyExtras(getIntent());
        getWindow().getDecorView().setBackgroundColor(BG);

        // the title over both columns, so the picture and the first rows start on the same line
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(56), dp(32), dp(56), 0);
        title = text("Wallhaven Shuffle", 15, FG3, false);
        page.addView(title);
        space(page, 14);
        LinearLayout columns = new LinearLayout(this);
        columns.setOrientation(LinearLayout.HORIZONTAL);
        page.addView(columns, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));
        // the picture and its buttons stay put; only the list of settings scrolls
        columns.addView(leftColumn(), new LinearLayout.LayoutParams(dp(500), ViewGroup.LayoutParams.WRAP_CONTENT));
        View gap = new View(this);
        columns.addView(gap, new LinearLayout.LayoutParams(dp(44), 1));
        ScrollView list = new ScrollView(this);
        list.setVerticalScrollBarEnabled(false);
        list.setClipToPadding(false);
        list.addView(rightColumn());
        columns.addView(list, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1));
        setContentView(page);
        refresh();
    }

    @Override protected void onStart() { super.onStart(); Sync.hold(this); Sync.listen(this); ui.post(tick); refresh(); }
    @Override protected void onStop() { Sync.unlisten(this); Sync.release(); ui.removeCallbacks(tick); super.onStop(); }
    @Override public void changed() { ui.post(this::refresh); }

    private final Runnable tick = new Runnable() {
        @Override public void run() { refreshStatus(); ui.postDelayed(this, 1000); }
    };

    // ---- left: the wallpaper on screen and what you can do with it

    private View leftColumn() {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);

        FrameLayout frame = new FrameLayout(this);
        frame.setBackground(round(RAISED, 14));
        frame.setClipToOutline(true);
        frame.setOutlineProvider(new ViewOutlineProvider() {
            @Override public void getOutline(View v, Outline o) { o.setRoundRect(0, 0, v.getWidth(), v.getHeight(), dp(14)); }
        });
        picture = new ImageView(this);
        picture.setScaleType(ImageView.ScaleType.CENTER_CROP);
        frame.addView(picture, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        pictureNote = text("", 16, FG3, false);
        pictureNote.setGravity(Gravity.CENTER);
        pictureNote.setPadding(dp(24), 0, dp(24), 0);
        frame.addView(pictureNote, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        box.addView(frame, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(281)));

        LinearLayout caption = new LinearLayout(this);
        captionLeft = text("", 15, FG2, false);
        captionRight = text("", 15, FG2, false);
        caption.addView(captionLeft, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        caption.addView(captionRight);
        caption.setPadding(0, dp(12), 0, 0);
        box.addView(caption);
        status = text("", 14, FG3, false);
        status.setPadding(0, dp(4), 0, dp(18));
        box.addView(status);

        // the same order as the PC and the remote: Keep, Previous, Pause, Next; then Favourites, playlists, Never show
        LinearLayout row1 = new LinearLayout(this), row2 = new LinearLayout(this);
        keepBtn = pill("Keep", v -> toggleKeep());
        TextView prev = pill("Previous", v -> { ShuffleService.previous(this); toast("Going back"); });
        pauseBtn = pill("Pause", v -> { TvSettings s = Sync.settings(this); s.paused = !s.paused; Sync.saveSettings(this, s, true); refresh(); });
        TextView next = pill("Next", v -> { ShuffleService.skipWait(this); toast("Next one coming up"); });
        row1.addView(keepBtn); row1.addView(prev); row1.addView(pauseBtn); row1.addView(next);
        favBtn = pill("Save to Favourites", v -> toggleFav());
        TextView add = pill("Add to playlist", v -> addToPlaylist());
        TextView hide = pill("Never show", v -> hideCurrent());
        row2.addView(favBtn); row2.addView(add); row2.addView(hide);
        row2.setPadding(0, dp(10), 0, 0);
        box.addView(row1);
        box.addView(row2);
        next.requestFocus();
        return box;
    }

    private void toggleKeep() {
        TvSettings s = Sync.settings(this);
        Shuffle.Item cur = ShuffleService.current(this);
        if (s.keep.isEmpty() && cur == null) { toast("Nothing on screen yet"); return; }
        s.keep = s.keep.isEmpty() ? cur.id : "";
        Sync.saveSettings(this, s, true);
        toast(s.keep.isEmpty() ? "Stopped keeping it" : "Keeping this wallpaper");
        refresh();
    }

    private void toggleFav() {
        Shuffle.Item cur = ShuffleService.current(this);
        if (!needsSync() || cur == null) return;
        boolean on = Sync.contains(this, "fav", cur.id);
        background(() -> Sync.op(this, opItem(on ? "remove" : "add", "fav", cur)), on ? "Removed from Favourites" : "Saved to Favourites");
    }

    private void hideCurrent() {
        Shuffle.Item cur = ShuffleService.current(this);
        if (!needsSync() || cur == null) return;
        background(() -> {
            String err = Sync.op(this, opItem("add", "hidden", cur));
            if (err == null) ShuffleService.skipWait(this);
            return err;
        }, "Hidden on every device");
    }

    private void addToPlaylist() {
        Shuffle.Item cur = ShuffleService.current(this);
        if (!needsSync() || cur == null) return;
        List<JSONObject> lists = Sync.ownPlaylists(this);
        if (lists.isEmpty()) { toast("Make a playlist on your phone or PC first"); return; }
        String[] names = new String[lists.size()];
        boolean[] on = new boolean[lists.size()];
        for (int i = 0; i < names.length; i++) { names[i] = lists.get(i).optString("name"); on[i] = Sync.contains(this, lists.get(i).optString("id"), cur.id); }
        dialog("Add to playlist").setMultiChoiceItems(names, on, (d, i, checked) ->
            background(() -> Sync.op(this, opItem(checked ? "add" : "remove", lists.get(i).optString("id"), cur)), checked ? "Added to " + names[i] : "Removed from " + names[i]))
            .setPositiveButton("Done", null).show();
    }

    private boolean needsSync() {
        if (Sync.paired(this)) return true;
        toast("Pair with your phone or PC first (Sync, on the right)");
        return false;
    }

    private static JSONObject opItem(String op, String pl, Shuffle.Item w) {
        try {
            JSONObject o = new JSONObject().put("op", op).put("pl", pl);
            return op.equals("add") ? o.put("item", Sync.itemJson(w)) : o.put("id", w.id);
        } catch (Exception e) { throw new IllegalStateException(e); }
    }

    // ---- right: the rows

    private View rightColumn() {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        LinearLayout g1 = group(box, null);
        row(g1, "Shuffle from", () -> sourcesLabel(), v -> pickSources());
        row(g1, "Change every", () -> TvSettings.everyLabel(Sync.settings(this).every), v -> pickEvery());

        LinearLayout g2 = group(box, "Toplist");
        row(g2, "Quality", () -> TvSettings.QUALITY.get(Sync.settings(this).quality), v -> pickMap("Quality", TvSettings.QUALITY, Sync.settings(this).quality, (s, k) -> s.quality = k));
        row(g2, "Shape", () -> TvSettings.RATIO.get(Sync.settings(this).ratio), v -> pickMap("Shape", TvSettings.RATIO, Sync.settings(this).ratio, (s, k) -> s.ratio = k));
        row(g2, "Period", () -> TvSettings.RANGE.get(Sync.settings(this).range), v -> pickMap("Period", TvSettings.RANGE, Sync.settings(this).range, (s, k) -> s.range = k));
        row(g2, "Popularity", () -> TvSettings.favLabel(Sync.settings(this).fav), v -> pickFav());
        row(g2, "Categories", () -> Sync.settings(this).catsLabel(), v -> pickCats());
        row(g2, "Topic", () -> Sync.settings(this).q.isEmpty() ? "Any" : Sync.settings(this).q, v -> pickTopic());
        row(g2, "Largest size", () -> TvSettings.maxwLabel(Sync.settings(this).maxw), v -> pickMaxw());

        LinearLayout g3 = group(box, "Sync");
        row(g3, () -> Sync.paired(this) ? "Synced as " + Sync.deviceName(this) : "Pair with your phone or PC",
            () -> Sync.paired(this) ? (Sync.reachable() ? "On" : "Can’t reach the service") : "", v -> { if (Sync.paired(this)) renameDevice(); else pair(); });
        row(g3, () -> Sync.paired(this) ? "Sign out" : "What sync does", () -> "", v -> {
            if (!Sync.paired(this)) { dialog("Sync").setMessage("Pair this TV and you can change what it shows from your phone, use the playlists you make on the PC, and send a wallpaper from one to the other.").setPositiveButton("OK", null).show(); return; }
            dialog("Sign out of sync?").setMessage("The TV keeps shuffling with its settings. Pair again any time.")
                .setPositiveButton("Sign out", (d, w) -> { Sync.signOut(this); refresh(); }).setNegativeButton("Cancel", null).show();
        });

        LinearLayout g4 = group(box, "Service");
        rowBelow(g4, "Address", () -> ListStore.server(this).replaceFirst("^https?://", ""), v -> editServer());
        row(g4, "Get a fresh list now", () -> listLabel(), v -> { toast("Getting the list…"); ListStore.refreshNow(this, r -> ui.post(() -> { toast(r); refresh(); })); });
        space(box, 40);   // the last row clear of the screen edge when the list is scrolled to the end
        return box;
    }

    private String sourcesLabel() {
        TvSettings s = Sync.settings(this);
        List<String> names = new ArrayList<>();
        for (String k : s.sources) {
            if (k.equals("toplist")) names.add("Toplist");
            else { JSONObject p = Sync.playlist(this, k); if (p != null) names.add(p.optString("name")); }
        }
        if (names.isEmpty()) return "Nothing yet";
        return names.size() <= 2 ? String.join(", ", names) : names.get(0) + " + " + (names.size() - 1) + " more";
    }

    private String listLabel() {
        int n = 0;
        TvSettings s = Sync.settings(this);
        for (Shuffle.Item w : ListStore.pool(this)) if (s.maxw == 0 || w.width <= s.maxw) n++;
        long t = ListStore.prefs(this).getLong("list_time", 0);
        if (n == 0) return "";
        // when it came: the time if today, otherwise the day
        boolean today = android.text.format.DateUtils.isToday(t);
        return String.format(Locale.US, "%,d", n) + (t > 0 ? " · " + android.text.format.DateFormat.format(today ? "HH:mm" : "d MMM", t) : "");
    }

    interface Apply<T> { void apply(TvSettings s, T value); }

    private void change(Apply<String> f, String v) { TvSettings s = Sync.settings(this); f.apply(s, v); Sync.saveSettings(this, s, true); refresh(); }

    private void pickMap(String title, java.util.Map<String, String> choices, String current, Apply<String> f) {
        final List<String> keys = new ArrayList<>(choices.keySet());
        String[] labels = new String[keys.size()];
        for (int i = 0; i < labels.length; i++) labels[i] = choices.get(keys.get(i));
        dialog(title).setSingleChoiceItems(labels, keys.indexOf(current), (d, i) -> { d.dismiss(); change(f, keys.get(i)); }).show();
    }

    private void pickInts(String title, int[] values, int current, java.util.function.IntFunction<String> label, Apply<Integer> f) {
        String[] labels = new String[values.length];
        int at = -1;
        for (int i = 0; i < values.length; i++) { labels[i] = label.apply(values[i]); if (values[i] == current) at = i; }
        dialog(title).setSingleChoiceItems(labels, at, (d, i) -> {
            d.dismiss();
            TvSettings s = Sync.settings(this); f.apply(s, values[i]); Sync.saveSettings(this, s, true); refresh();
        }).show();
    }

    private void pickEvery() {
        long base = ShuffleService.base(this);
        pickInts("Change every", TvSettings.EVERY, Sync.settings(this).every, TvSettings::everyLabel, (s, v) -> s.every = v);
        if (base >= 90_000) toast("Projectivy asks about every " + Math.round(base / 60000.0) + " min, so the TV can’t change faster than that");
    }
    private void pickFav() { pickInts("Popularity", TvSettings.FAVS, Sync.settings(this).fav, TvSettings::favLabel, (s, v) -> s.fav = v); }
    private void pickMaxw() { pickInts("Largest size", TvSettings.MAXW, Sync.settings(this).maxw, TvSettings::maxwLabel, (s, v) -> s.maxw = v); }

    private void pickSources() {
        TvSettings s = Sync.settings(this);
        final List<String> keys = new ArrayList<>();
        List<String> labels = new ArrayList<>();
        keys.add("toplist"); labels.add("Toplist");
        if (Sync.playlist(this, "fav") != null) { keys.add("fav"); labels.add("Favourites  (" + Sync.items(this, "fav").size() + ")"); }
        for (JSONObject p : Sync.ownPlaylists(this)) { keys.add(p.optString("id")); labels.add(p.optString("name") + "  (" + p.optJSONArray("items").length() + ")"); }
        boolean[] on = new boolean[keys.size()];
        for (int i = 0; i < on.length; i++) on[i] = s.sources.contains(keys.get(i));
        AlertDialog.Builder b = dialog("Shuffle from").setMultiChoiceItems(labels.toArray(new String[0]), on, (d, i, checked) -> on[i] = checked)
            .setPositiveButton("Done", (d, w) -> {
                TvSettings t = Sync.settings(this);
                t.sources.clear();
                for (int i = 0; i < on.length; i++) if (on[i]) t.sources.add(keys.get(i));
                Sync.saveSettings(this, t, true);
                refresh();
            }).setNegativeButton("Cancel", null);
        if (!Sync.paired(this)) b.setMessage("Pair with your phone or PC to shuffle your playlists too.");
        b.show();
    }

    private void pickCats() {
        String[] names = {"General", "Anime", "People"};
        String cats = Sync.settings(this).cats;
        boolean[] on = {cats.charAt(0) == '1', cats.charAt(1) == '1', cats.charAt(2) == '1'};
        dialog("Categories").setMultiChoiceItems(names, on, (d, i, checked) -> on[i] = checked)
            .setPositiveButton("Done", (d, w) -> {
                String next = (on[0] ? "1" : "0") + (on[1] ? "1" : "0") + (on[2] ? "1" : "0");
                if (next.equals("000")) { toast("Keep at least one category"); return; }
                change((s, v) -> s.cats = v, next);
            }).setNegativeButton("Cancel", null).show();
    }

    private void pickTopic() {
        String[] labels = new String[TvSettings.TOPICS.length + 2];
        labels[0] = "Any";
        for (int i = 0; i < TvSettings.TOPICS.length; i++) labels[i + 1] = TvSettings.TOPICS[i].substring(0, 1).toUpperCase(Locale.ROOT) + TvSettings.TOPICS[i].substring(1);
        labels[labels.length - 1] = "Type one…";
        String q = Sync.settings(this).q;
        int at = q.isEmpty() ? 0 : -1;
        for (int i = 0; i < TvSettings.TOPICS.length; i++) if (TvSettings.TOPICS[i].equals(q)) at = i + 1;
        dialog("Topic").setSingleChoiceItems(labels, at, (d, i) -> {
            d.dismiss();
            if (i == 0) change((s, v) -> s.q = v, "");
            else if (i == labels.length - 1) editText("Topic", q, "A Wallhaven search, like nature or night city", InputType.TYPE_CLASS_TEXT, v -> change((s, x) -> s.q = x, v));
            else change((s, v) -> s.q = v, TvSettings.TOPICS[i - 1]);
        }).show();
    }

    private void editServer() {
        editText("Service address", ListStore.server(this), "https://<your-project>.vercel.app", InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI, v -> {
            String s = v.trim().replaceAll("/+$", "");
            if (!s.isEmpty() && !s.startsWith("http")) s = "https://" + s;
            ListStore.prefs(this).edit().putString("server", s).putLong("list_time", 0).apply();
            ListStore.refreshNow(this, r -> ui.post(() -> { toast(r); refresh(); }));
            refresh();
        });
    }

    private void renameDevice() {
        editText("Name this TV", Sync.deviceName(this), "Living room TV", InputType.TYPE_CLASS_TEXT, v -> {
            if (v.trim().isEmpty()) return;
            background(() -> {
                try { return Sync.op(this, new JSONObject().put("op", "name").put("device", ListStore.prefs(this).getString("sync_id", "")).put("name", v.trim())); }
                catch (Exception e) { return e.getMessage(); }
            }, "Renamed");
        });
    }

    // ---- pairing: a code to type on the phone or the PC

    private void pair() {
        if (ListStore.server(this).isEmpty()) { toast("Set the service address first (Service, below)"); return; }
        LinearLayout v = new LinearLayout(this);
        v.setOrientation(LinearLayout.VERTICAL);
        v.setPadding(dp(28), dp(12), dp(28), dp(8));
        TextView code = text("––– –––", 52, FG, true);
        code.setLetterSpacing(0.08f);
        TextView how = text("On your phone, open the remote (your service address) and choose Devices > Pair a device.\nOn the PC: right-click the tray icon, then Remote > Pair a device.", 16, FG2, false);
        TextView note = text("Getting a code…", 14, FG3, false);
        v.addView(code); space(v, 10); v.addView(how); space(v, 12); v.addView(note);
        final int[] round = {0};
        pairDialog = dialog("Pair this TV").setView(v).setNegativeButton("Cancel", null).create();
        pairDialog.setOnDismissListener(d -> round[0]++);
        pairDialog.show();
        startPairing(code, note, round);
    }

    private void startPairing(TextView code, TextView note, int[] round) {
        final int mine = ++round[0];
        new Thread(() -> {
            try {
                JSONObject p = Sync.startPairing(this);
                String c = p.getString("code");
                long expires = p.getLong("expires");
                ui.post(() -> { code.setText(c.substring(0, 3) + " " + c.substring(3)); note.setText("The code works for 10 minutes."); });
                while (mine == round[0]) {
                    Thread.sleep(2500);
                    if (mine != round[0]) return;
                    if (System.currentTimeMillis() > expires - 5000) { ui.post(() -> startPairing(code, note, round)); return; }
                    if (Sync.claim(this, p.getString("claim"))) {
                        ui.post(() -> { if (pairDialog != null) pairDialog.dismiss(); toast("Paired as " + Sync.deviceName(this)); refresh(); });
                        return;
                    }
                }
            } catch (Exception e) {
                ui.post(() -> note.setText("Couldn’t get a code: " + e.getMessage()));
            }
        }, "pairing").start();
    }

    // ---- keeping the screen current

    private void refresh() {
        if (isFinishing()) return;
        for (Runnable r : refreshers) r.run();
        TvSettings s = Sync.settings(this);
        Shuffle.Item cur = ShuffleService.current(this);
        keepBtn.setText(s.keep.isEmpty() ? "Keep" : "Stop keeping");
        pauseBtn.setText(s.paused ? "Resume" : "Pause");
        keepBtn.setTextColor(s.keep.isEmpty() ? FG : ACCENT);
        boolean fav = cur != null && Sync.contains(this, "fav", cur.id);
        favBtn.setText(fav ? "In Favourites" : "Save to Favourites");
        favBtn.setTextColor(fav ? HEART : FG);
        if (cur == null) {
            picture.setImageDrawable(null);
            pictureId = "";
            pictureNote.setText(ListStore.server(this).isEmpty() ? "Set the service address to start" : "The wallpaper on screen shows here");
            captionLeft.setText(""); captionRight.setText("");
        } else {
            pictureNote.setText("");
            // the heart as text (not the red emoji), in the same pink as on the PC and in the remote
            String res = cur.resolution.isEmpty() ? "" : cur.resolution.replace("x", " × ");
            android.text.SpannableStringBuilder cap = new android.text.SpannableStringBuilder(res);
            if (cur.favorites > 0) {
                cap.append(res.isEmpty() ? "" : "     ");
                int at = cap.length();
                cap.append("♥︎");
                cap.setSpan(new android.text.style.ForegroundColorSpan(HEART), at, cap.length(), 0);
                cap.append(String.format(Locale.US, " %,d", cur.favorites));
            }
            captionLeft.setText(cap);
            int shown = ListStore.prefs(this).getInt("shown", 0), total = ListStore.prefs(this).getInt("total", 0);
            if (total == 0) total = ShuffleService.pool(this, Sync.settings(this)).size();   // until the first change of 1.8
            captionRight.setText(total > 0 ? String.format(Locale.US, "%,d / %,d", Math.min(shown, total), total) : "");
            if (!cur.id.equals(pictureId)) loadPicture(cur);
        }
        refreshStatus();
    }

    private void refreshStatus() {
        if (status == null) return;
        TvSettings s = Sync.settings(this);
        if (s.paused) { status.setText("Paused"); return; }
        if (!s.keep.isEmpty()) { status.setText("Keeping this wallpaper"); return; }
        JSONObject st = ShuffleService.status(this);
        if (st.has("note")) { status.setText(st.optString("note")); status.setTextColor(WARN); return; }
        status.setTextColor(FG3);
        long next = st.optLong("next", 0), left = (next - System.currentTimeMillis()) / 1000;
        status.setText(next == 0 ? "" : left <= 0 ? "Changing at Projectivy’s next turn" : String.format(Locale.US, "Next in %d:%02d", left / 60, left % 60));
    }

    private void loadPicture(Shuffle.Item w) {
        pictureId = w.id;
        final String id = w.id;
        new Thread(() -> {
            try {
                HttpURLConnection http = (HttpURLConnection) new URL("https://th.wallhaven.cc/lg/" + id.substring(0, 2) + "/" + id + ".jpg").openConnection();
                http.setConnectTimeout(10_000); http.setReadTimeout(15_000);
                Bitmap b;
                try (InputStream in = http.getInputStream()) { b = BitmapFactory.decodeStream(in); } finally { http.disconnect(); }
                ui.post(() -> { if (id.equals(pictureId) && b != null) { picture.setAlpha(0f); picture.setImageBitmap(b); picture.animate().alpha(1f).setDuration(300).start(); } });
            } catch (Exception e) { ui.post(() -> { if (id.equals(pictureId)) pictureId = ""; }); }
        }, "picture").start();
    }

    // Settings given on the command line (ADB): saved straight away, and the list is fetched.
    private void applyExtras(Intent i) {
        if (i == null || !(i.hasExtra("server") || i.hasExtra("list_url"))) return;
        if (i.hasExtra("server")) ListStore.prefs(this).edit().putString("server", i.getStringExtra("server").trim().replaceAll("/+$", "")).apply();
        if (i.hasExtra("list_url")) ListStore.prefs(this).edit().putString("list_url", i.getStringExtra("list_url").trim()).apply();
        ListStore.prefs(this).edit().putLong("list_time", 0).apply();
        ListStore.refreshNow(this, null);
    }

    @Override protected void onNewIntent(Intent intent) { super.onNewIntent(intent); setIntent(intent); applyExtras(intent); refresh(); }

    // ---- small view helpers

    interface Text { String get(); }
    interface Result { String run(); }

    // Runs a network change off the main thread, then says how it went.
    private void background(Result work, String ok) {
        new Thread(() -> { String err = work.run(); ui.post(() -> { toast(err == null ? ok : err); refresh(); }); }, "change").start();
    }

    private void editText(String title, String value, String hint, int type, java.util.function.Consumer<String> done) {
        EditText e = new EditText(this);
        e.setText(value); e.setHint(hint); e.setInputType(type); e.setSingleLine(true); e.setSelectAllOnFocus(true);
        FrameLayout pad = new FrameLayout(this);
        pad.setPadding(dp(24), dp(8), dp(24), 0);
        pad.addView(e);
        dialog(title).setView(pad).setPositiveButton("Save", (d, w) -> done.accept(e.getText().toString())).setNegativeButton("Cancel", null).show();
    }

    private AlertDialog.Builder dialog(String title) { return new AlertDialog.Builder(this, android.R.style.Theme_DeviceDefault_Dialog_Alert).setTitle(title); }
    private void toast(String s) { if (s != null && !s.isEmpty()) Toast.makeText(this, s, Toast.LENGTH_SHORT).show(); }
    private int dp(int v) { return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, getResources().getDisplayMetrics()); }
    private void space(LinearLayout box, int h) { box.addView(new View(this), new LinearLayout.LayoutParams(1, dp(h))); }

    private TextView text(String s, int sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(s); t.setTextSize(sp); t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.create("sans-serif-medium", Typeface.BOLD));
        return t;
    }

    private GradientDrawable round(int color, int radius) {
        GradientDrawable d = new GradientDrawable();
        d.setColor(color); d.setCornerRadius(dp(radius));
        return d;
    }

    // A button under the picture: a quiet pill that turns white when the remote is on it.
    private TextView pill(String label, View.OnClickListener click) {
        TextView t = text(label, 16, FG, false);
        t.setFocusable(true); t.setClickable(true);
        t.setPadding(dp(18), dp(10), dp(18), dp(10));
        StateListDrawable d = new StateListDrawable();
        d.addState(new int[]{android.R.attr.state_focused}, round(FG, 22));
        d.addState(new int[]{}, round(RAISED, 22));
        t.setBackground(d);
        t.setOnFocusChangeListener((v, has) -> { ((TextView) v).setTextColor(has ? BG : (v == keepBtn && !Sync.settings(this).keep.isEmpty() ? ACCENT : v == favBtn && favBtn.getText().toString().startsWith("In") ? HEART : FG)); });
        t.setOnClickListener(click);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.setMargins(0, 0, dp(10), 0);
        t.setLayoutParams(lp);
        return t;
    }

    private LinearLayout group(LinearLayout parent, String label) {
        if (label != null) {
            TextView l = text(label, 15, FG2, false);
            l.setPadding(dp(4), dp(26), 0, dp(10));
            parent.addView(l);
        }
        LinearLayout g = new LinearLayout(this);
        g.setOrientation(LinearLayout.VERTICAL);
        g.setBackground(round(RAISED, 14));
        g.setPadding(dp(6), dp(6), dp(6), dp(6));
        parent.addView(g);
        return g;
    }

    private void row(LinearLayout group, String label, Text value, View.OnClickListener click) { row(group, () -> label, value, click); }

    // A row whose value is too long for the right side (the address): the value on its own line under the label.
    private void rowBelow(LinearLayout group, String label, Text value, View.OnClickListener click) {
        LinearLayout r = new LinearLayout(this);
        r.setOrientation(LinearLayout.VERTICAL);
        r.setFocusable(true); r.setClickable(true);
        r.setPadding(dp(16), dp(11), dp(16), dp(11));
        StateListDrawable d = new StateListDrawable();
        d.addState(new int[]{android.R.attr.state_focused}, round(FOCUS, 10));
        d.addState(new int[]{}, round(Color.TRANSPARENT, 10));
        r.setBackground(d);
        TextView l = text(label, 17, FG, false), v = text("", 15, FG2, false);
        v.setSingleLine(true); v.setEllipsize(android.text.TextUtils.TruncateAt.MIDDLE);
        r.addView(l); r.addView(v);
        r.setOnFocusChangeListener((x, has) -> v.setTextColor(has ? FG : FG2));
        r.setOnClickListener(click);
        group.addView(r);
        refreshers.add(() -> v.setText(value.get()));
    }

    // A row: label on the left, its current value on the right; lighter when the remote is on it.
    private void row(LinearLayout group, Text label, Text value, View.OnClickListener click) {
        LinearLayout r = new LinearLayout(this);
        r.setGravity(Gravity.CENTER_VERTICAL);
        r.setFocusable(true); r.setClickable(true);
        r.setPadding(dp(16), dp(13), dp(16), dp(13));
        StateListDrawable d = new StateListDrawable();
        d.addState(new int[]{android.R.attr.state_focused}, round(FOCUS, 10));
        d.addState(new int[]{}, round(Color.TRANSPARENT, 10));
        r.setBackground(d);
        TextView l = text("", 17, FG, false), v = text("", 16, FG2, false);
        v.setGravity(Gravity.END); v.setSingleLine(true); v.setEllipsize(android.text.TextUtils.TruncateAt.END);
        r.addView(l, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        LinearLayout.LayoutParams vp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1);
        vp.setMargins(dp(20), 0, 0, 0);
        r.addView(v, vp);
        r.setOnFocusChangeListener((x, has) -> v.setTextColor(has ? FG : FG2));
        r.setOnClickListener(click);
        group.addView(r);
        refreshers.add(() -> { l.setText(label.get()); v.setText(value.get()); });
    }
}
