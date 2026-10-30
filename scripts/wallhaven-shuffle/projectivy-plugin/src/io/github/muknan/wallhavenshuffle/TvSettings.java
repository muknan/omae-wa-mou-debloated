package io.github.muknan.wallhavenshuffle;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * What this TV shuffles and how: its sources, toplist filters, how often it changes, pause and keep. The same
 * settings (and the same choices) as the service's lib/sync.js, which keeps a copy per device so the remote and the
 * Windows app can change them. Plain Java (no Android), so it's tested on the PC.
 */
final class TvSettings {
    // The choices, in the order the settings screen lists them; labels match the remote's.
    static final Map<String, String> QUALITY = new LinkedHashMap<>();
    static final Map<String, String> QUALITY_SIZE = new LinkedHashMap<>();   // "atleast" size, or "=" + exact sizes
    static final Map<String, String> RATIO = new LinkedHashMap<>();
    static final Map<String, String> RANGE = new LinkedHashMap<>();
    static final int[] FAVS = {0, 10, 25, 50, 100, 250, 500, 1000};
    static final int[] MAXW = {0, 3840, 5120};
    static final int[] EVERY = {1, 2, 3, 5, 10, 15, 20, 30, 45, 60, 120, 180, 360, 720, 1440};
    static final String[] TOPICS = {"nature", "landscape", "mountains", "forest", "ocean", "space", "city", "night", "sunset", "winter", "architecture", "minimalism", "abstract", "digital art", "cars"};
    static {
        quality("1080+", "Full HD and up", "1920x1080");
        quality("1440+", "1440p and up", "2560x1440");
        quality("4k+", "4K and up", "3840x2160");
        quality("4k", "Exactly 4K", "=3840x2160,3840x2400");
        quality("5k+", "5K and up", "5120x2880");
        quality("8k", "8K", "7680x4320");
        RATIO.put("16x9,16x10", "Widescreen");
        RATIO.put("16x9", "16:9 (TV)");
        RATIO.put("16x10", "16:10 (laptop)");
        RATIO.put("21x9,32x9", "Ultrawide");
        RATIO.put("", "Any");
        RANGE.put("1d", "Past day"); RANGE.put("3d", "Past 3 days"); RANGE.put("1w", "Past week"); RANGE.put("1M", "Past month");
        RANGE.put("3M", "Past 3 months"); RANGE.put("6M", "Past 6 months"); RANGE.put("1y", "Past year");
    }
    private static void quality(String key, String label, String size) { QUALITY.put(key, label); QUALITY_SIZE.put(key, size); }

    List<String> sources = new ArrayList<>();
    String quality = "4k+", ratio = "16x9,16x10", range = "1y", cats = "110", q = "";
    int fav = 50, maxw = 3840, every = 1;
    boolean paused;
    String keep = "";

    /** A TV's first settings: the toplist the plugin always showed (a year of 4K and up, widescreen, no People, 50+). */
    static TvSettings defaults() {
        TvSettings s = new TvSettings();
        s.sources.add("toplist");
        return s;
    }

    TvSettings copy() {
        TvSettings s = new TvSettings();
        s.sources = new ArrayList<>(sources);
        s.quality = quality; s.ratio = ratio; s.range = range; s.cats = cats; s.q = q;
        s.fav = fav; s.maxw = maxw; s.every = every; s.paused = paused; s.keep = keep;
        return s;
    }

    /** Keeps every field to a value the service accepts (what came from elsewhere may be anything). */
    TvSettings clean() {
        if (!QUALITY.containsKey(quality)) quality = "4k+";
        if (!RATIO.containsKey(ratio)) ratio = "16x9,16x10";
        if (!RANGE.containsKey(range)) range = "1y";
        if (cats == null || !cats.matches("[01]{3}") || cats.equals("000")) cats = "110";
        q = normTopic(q);
        if (fav < 0 || fav > 100000) fav = 50;
        boolean okW = false;
        for (int w : MAXW) okW |= w == maxw;
        if (!okW) maxw = 3840;
        if (every < 1 || every > 1440) every = 1;
        if (keep == null) keep = "";
        List<String> ok = new ArrayList<>();
        for (String k : sources) if (k != null && k.matches("toplist|fav|p_[a-z0-9]{4,24}") && !ok.contains(k)) ok.add(k);
        sources = ok;
        return this;
    }

    /** A topic as every app writes it, so one topic is one cached list (lib/core.js normTopic). */
    static String normTopic(String q) {
        String t = q == null ? "" : q.toLowerCase(Locale.ROOT).replaceAll("\\s+", " ").trim();
        return t.matches("[a-z0-9 -]{1,40}") ? t : "";
    }

    /** The /api/list query for these filters, character for character the service's listQueryFor (lib/sync.js),
     *  so the TV, the PC and the remote share one cached list. No width limit: the TV applies maxw itself. */
    String listQuery() {
        String size = QUALITY_SIZE.get(quality);
        StringBuilder b = new StringBuilder("range=").append(range);
        if (size.startsWith("=")) b.append("&res=").append(size.substring(1));
        else b.append("&atleast=").append(size);
        if (!ratio.isEmpty()) b.append("&ratios=").append(ratio);
        b.append("&cats=").append(cats).append("&purity=100");
        if (!q.isEmpty()) b.append("&q=").append(q.replace(" ", "%20"));
        b.append("&fav=").append(fav).append("&maxw=0");
        return b.toString();
    }

    // ---- labels for the settings screen

    static String everyLabel(int m) {
        if (m < 60) return m == 1 ? "Every minute" : "Every " + m + " minutes";
        if (m < 1440) return m == 60 ? "Every hour" : "Every " + (m / 60) + " hours";
        return "Once a day";
    }
    static String favLabel(int n) { return n == 0 ? "Any" : String.format(Locale.US, "%,d+ favourites", n); }
    static String maxwLabel(int w) { return w == 0 ? "Any" : w == 3840 ? "Up to 4K" : "Up to 5K"; }
    String catsLabel() {
        List<String> on = new ArrayList<>();
        String[] names = {"General", "Anime", "People"};
        for (int i = 0; i < 3; i++) if (cats.charAt(i) == '1') on.add(names[i]);
        return String.join(", ", on);
    }
    String filtersLabel() {
        return QUALITY.get(quality) + " · " + RANGE.get(range) + (q.isEmpty() ? "" : " · “" + q + "”");
    }
}
