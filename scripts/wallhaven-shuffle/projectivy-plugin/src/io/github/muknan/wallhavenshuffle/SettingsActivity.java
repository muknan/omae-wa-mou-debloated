package io.github.muknan.wallhavenshuffle;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.text.InputType;
import android.text.format.DateFormat;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

/**
 * Settings, opened from Projectivy (the plugin's "Configure") or the app list. Plain views built in code (no
 * libraries), large enough to read from the sofa and fully usable with the remote. For setting it up over ADB:
 *   am start -n io.github.muknan.wallhavenshuffle/.SettingsActivity --es list_url "https://.../api/list?..." --ei max_width 3840
 */
public final class SettingsActivity extends Activity {
    private EditText url, width;
    private TextView status;

    @Override protected void onCreate(Bundle saved) {
        super.onCreate(saved);
        if (saved == null) applyExtras(getIntent());   // once: not again when Android re-creates the screen

        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(dp(64), dp(40), dp(64), dp(40));
        box.setBackgroundColor(Color.rgb(18, 18, 22));

        box.addView(text("Wallhaven Shuffle", 30, Color.WHITE, true));
        box.addView(text("Wallpapers for Projectivy in shuffled order: every one once, no repeats until all were shown.", 15, Color.rgb(170, 170, 180), false));
        status = text("", 17, Color.rgb(140, 200, 255), false);
        status.setPadding(0, dp(18), 0, dp(18));
        box.addView(status);

        SharedPreferences p = ListStore.prefs(this);
        box.addView(text("List address (a wallhaven-shuffle /api/list link)", 15, Color.rgb(200, 200, 210), false));
        url = field(p.getString("list_url", ""), InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        box.addView(url);
        box.addView(text("Widest wallpaper to show, in pixels (0 = any; 3840 skips 5K/8K, which a TV decodes slowly)", 15, Color.rgb(200, 200, 210), false));
        width = field(String.valueOf(p.getInt("max_width", 3840)), InputType.TYPE_CLASS_NUMBER);
        box.addView(width);

        LinearLayout buttons = new LinearLayout(this);
        buttons.setPadding(0, dp(20), 0, dp(12));
        Button save = button("Save"), refresh = button("Refresh list now"), next = button("Next wallpaper");
        buttons.addView(save); buttons.addView(refresh); buttons.addView(next);
        box.addView(buttons);
        box.addView(text("To use it: Projectivy settings > Appearance > Wallpaper > Launcher wallpaper > Wallhaven Shuffle. "
            + "Set Projectivy's \"Update delay\" to how often you want a new wallpaper.", 14, Color.rgb(150, 150, 160), false));

        save.setOnClickListener(v -> { save(); ListStore.refreshNow(this, r -> runOnUiThread(() -> showStatus(r))); });
        refresh.setOnClickListener(v -> { status.setText("Getting the list…"); ListStore.refreshNow(this, r -> runOnUiThread(() -> showStatus(r))); });
        next.setOnClickListener(v -> { ShuffleService.askProjectivyToRefresh(this, 1); status.setText("Asked Projectivy for the next wallpaper"); });

        ScrollView scroll = new ScrollView(this);
        scroll.addView(box);
        setContentView(scroll);
        save.requestFocus();
        showStatus(null);
    }

    // Opened again while open (singleTask): take the new settings and show them. Not recreate(): that would run
    // onCreate with the screen's first intent and put its (older) settings back.
    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        if (!applyExtras(intent)) return;
        SharedPreferences p = ListStore.prefs(this);
        url.setText(p.getString("list_url", ""));
        width.setText(String.valueOf(p.getInt("max_width", 3840)));
        status.setText("Getting the list…");
    }

    // Settings given on the command line (ADB): saved straight away, and the list is fetched.
    private boolean applyExtras(Intent i) {
        if (i == null || !(i.hasExtra("list_url") || i.hasExtra("max_width"))) return false;
        SharedPreferences.Editor e = ListStore.prefs(this).edit();
        if (i.hasExtra("list_url")) e.putString("list_url", i.getStringExtra("list_url").trim());
        if (i.hasExtra("max_width")) e.putInt("max_width", i.getIntExtra("max_width", 3840));
        if (i.hasExtra("min_favorites")) e.putInt("min_favorites", i.getIntExtra("min_favorites", 0));
        e.putLong("list_time", 0).apply();
        ListStore.refreshNow(this, r -> runOnUiThread(() -> showStatus(r)));
        return true;
    }

    private void save() {
        int w;
        try { w = Math.max(0, Integer.parseInt(width.getText().toString().trim())); } catch (NumberFormatException e) { w = 3840; }
        ListStore.prefs(this).edit().putString("list_url", url.getText().toString().trim()).putInt("max_width", w).putLong("list_time", 0).apply();
        status.setText("Saved. Getting the list…");
        ShuffleService.askProjectivyToRefresh(this, 1);
    }

    private void showStatus(String result) {
        if (isFinishing()) return;
        SharedPreferences p = ListStore.prefs(this);
        int size = ListStore.pool(this).size();
        long time = p.getLong("list_time", 0);
        StringBuilder s = new StringBuilder();
        if (p.getString("list_url", "").isEmpty()) s.append("Add the list address below to start.");
        else if (size == 0) s.append("No list yet.");
        else {
            s.append(size).append(" wallpapers · ").append(Math.min(p.getInt("shown", 0), size)).append(" shown this round");
            if (time > 0) s.append(" · list from ").append(DateFormat.format("d MMM, HH:mm", time));
        }
        if (result != null && !result.startsWith("list:")) s.append("\n").append(result);   // errors only: the line above says the rest
        status.setText(s);
    }

    // ---- small view helpers

    private int dp(int v) { return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, getResources().getDisplayMetrics()); }

    private TextView text(String s, int sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(s); t.setTextSize(sp); t.setTextColor(color);
        if (bold) t.setTypeface(t.getTypeface(), android.graphics.Typeface.BOLD);
        t.setPadding(0, dp(4), 0, dp(4));
        return t;
    }

    private EditText field(String value, int type) {
        EditText e = new EditText(this);
        e.setText(value); e.setInputType(type); e.setSingleLine(true);
        e.setTextSize(17); e.setTextColor(Color.WHITE);
        e.setPadding(dp(14), dp(10), dp(14), dp(10));
        e.setBackground(focusable(Color.rgb(40, 40, 48)));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.setMargins(0, dp(4), 0, dp(14));
        e.setLayoutParams(lp);
        return e;
    }

    private Button button(String label) {
        Button b = new Button(this);
        b.setText(label); b.setAllCaps(false); b.setTextSize(16); b.setTextColor(Color.WHITE);
        b.setBackground(focusable(Color.rgb(55, 55, 66)));
        b.setPadding(dp(22), dp(10), dp(22), dp(10));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.setMargins(0, 0, dp(16), 0);
        b.setLayoutParams(lp);
        b.setGravity(Gravity.CENTER);
        return b;
    }

    // A rounded background that lights up (accent outline) when focused with the remote.
    private android.graphics.drawable.StateListDrawable focusable(int fill) {
        android.graphics.drawable.StateListDrawable d = new android.graphics.drawable.StateListDrawable();
        GradientDrawable on = new GradientDrawable(), off = new GradientDrawable();
        on.setColor(Color.rgb(Color.red(fill) + 25, Color.green(fill) + 25, Color.blue(fill) + 35)); on.setCornerRadius(dp(10));
        on.setStroke(dp(3), Color.rgb(140, 200, 255));
        off.setColor(fill); off.setCornerRadius(dp(10));
        d.addState(new int[]{android.R.attr.state_focused}, on);
        d.addState(new int[]{}, off);
        return d;
    }
}
