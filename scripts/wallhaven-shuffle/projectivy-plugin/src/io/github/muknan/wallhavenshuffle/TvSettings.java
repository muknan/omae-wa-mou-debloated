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
    /** The order the pool is shown in (the same words as the remote's Browse): shuffled, or the most favourited first. */
    static final Map<String, String> ORDER = new LinkedHashMap<>();
    static final int[] FAVS = {0, 10, 25, 50, 100, 250, 500, 1000};
    static final int[] MAXW = {0, 3840, 5120};
    static final int[] EVERY = {1, 2, 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440};
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
        ORDER.put("shuffle", "Shuffled"); ORDER.put("fav", "Most favourites");
    }
    private static void quality(String key, String label, String size) { QUALITY.put(key, label); QUALITY_SIZE.put(key, size); }

    List<String> sources = new ArrayList<>();
    String quality = "4k+", ratio = "16x9,16x10", range = "1y", cats = "110", q = "";
    int fav = 50, favmax = 0, maxw = 3840, every = 1;   // Popularity: at least fav favourites and, if favmax > 0, at most favmax
    boolean paused;
    String keep = "", order = "shuffle";

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
        s.fav = fav; s.favmax = favmax; s.maxw = maxw; s.every = every; s.paused = paused; s.keep = keep; s.order = order;
        return s;
    }

    /** Keeps every field to a value the service accepts (what came from elsewhere may be anything). */
    TvSettings clean() {
        if (!QUALITY.containsKey(quality)) quality = "4k+";
        if (!RATIO.containsKey(ratio)) ratio = "16x9,16x10";
        if (!RANGE.containsKey(range)) range = "1y";
        if (cats == null || !cats.matches("[01]{3}") || cats.equals("000")) cats = "110";
        q = normTopic(q);
        if (fav < 0 || fav > FAV_MAX) fav = 50;
        if (favmax < 0 || favmax > FAV_MAX || favmax < fav) favmax = 0;   // a minimum raised past it: no upper limit
        if (!ORDER.containsKey(order)) order = "shuffle";
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
     *  so the TV, the PC and the remote share one cached list. No width limit or favourites maximum: the TV applies
     *  maxw and favmax itself. */
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

    // The same words as the PC and the remote ("Change every" … "15 minutes").
    static String everyLabel(int m) {
        if (m < 60) return m == 1 ? "1 minute" : m + " minutes";
        if (m < 1440) return m == 60 ? "1 hour" : (m / 60) + " hours";
        return "1 day";
    }
    // A number typed in: digits (commas and spaces allowed, as in 1,500), 0 to 100,000; -1 if it isn't one.
    static final int FAV_MAX = 100000;
    static final String FAV_NOTE = "How many people favourited a wallpaper on Wallhaven. Leave At most empty for no upper limit.";
    static int parseFav(String v) {
        String t = v == null ? "" : v.replaceAll("[\\s,]", "");
        if (!t.matches("\\d{1,6}")) return -1;
        int n = Integer.parseInt(t);
        return n <= FAV_MAX ? n : -1;
    }
    /** Type a range…'s two fields: {min, max} (an empty At least is 0, an empty At most no limit), or null and why in err[0]. */
    static int[] parseRange(String minText, String maxText, String[] err) {
        boolean hasMin = minText != null && !minText.trim().isEmpty(), hasMax = maxText != null && !maxText.trim().isEmpty();
        int min = hasMin ? parseFav(minText) : 0, max = hasMax ? parseFav(maxText) : 0;
        String why = null;
        if (min < 0) why = String.format(Locale.US, "At least: a whole number from 0 to %,d", FAV_MAX);
        else if (max < 0) why = String.format(Locale.US, "At most: a whole number up to %,d, or empty", FAV_MAX);
        else if (hasMax && max < Math.max(min, 1)) why = String.format(Locale.US, "At most: %,d or more, or empty", Math.max(min, 1));
        if (err != null && err.length > 0) err[0] = why;
        return why == null ? new int[]{min, max} : null;
    }
    /** Popularity's words, the same as the remote's and the PC's: Any, 250+ favourites, Up to 500 favourites, 250 to 1,000 favourites. */
    static String favLabel(int min, int max) {
        if (max > 0) return min > 0 ? String.format(Locale.US, "%,d to %,d favourites", min, max) : String.format(Locale.US, "Up to %,d favourites", max);
        return min == 0 ? "Any" : String.format(Locale.US, "%,d+ favourites", min);
    }
    static String favLabel(int n) { return favLabel(n, 0); }
    String popularityLabel() { return favLabel(fav, favmax); }
    /** A toplist wallpaper inside the limits the TV applies itself: the largest size and the most favourites. */
    boolean inLimits(Shuffle.Item w) { return (maxw == 0 || w.width <= maxw) && (favmax == 0 || w.favorites <= favmax); }
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
