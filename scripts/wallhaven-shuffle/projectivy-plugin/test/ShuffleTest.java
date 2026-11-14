package io.github.muknan.wallhavenshuffle;

import java.util.*;

/** Runs on the PC (build.ps1 -Test): the rotation rules the TV relies on. */
public class ShuffleTest {
    static int fail = 0;

    static void check(String label, boolean ok, Object info) {
        if (!ok) fail++;
        System.out.printf("%s %s  %s%n", ok ? "ok  " : "FAIL", label, info);
    }

    static List<Shuffle.Item> pool(int n) {
        List<Shuffle.Item> p = new ArrayList<>();
        for (int i = 0; i < n; i++) p.add(new Shuffle.Item("w" + i, "https://x/w" + i + ".jpg", "https://p/w" + i, "3840x2160", 3840, 100));
        return p;
    }

    public static void main(String[] args) {
        List<Shuffle.Item> p = pool(40);
        Shuffle s = new Shuffle(List.of(), List.of(), new Random(1));
        List<String> shown = new ArrayList<>();
        for (int i = 0; i < 400; i++) shown.add(s.next(p).id);

        int dups = 0;
        for (int r = 0; r < 10; r++) dups += 40 - new HashSet<>(shown.subList(r * 40, r * 40 + 40)).size();
        check("10 rounds of 40: each wallpaper exactly once per round", dups == 0, "repeats within rounds: " + dups);

        Map<String, Integer> last = new HashMap<>();
        int minGap = Integer.MAX_VALUE;
        for (int i = 0; i < shown.size(); i++) {
            Integer j = last.put(shown.get(i), i);
            if (j != null) minGap = Math.min(minGap, i - j);
        }
        check("nothing comes back within 20 of itself, also across rounds", minGap > 20, "shortest gap " + minGap);

        // State survives a restart (the service keeps it in its preferences).
        Shuffle a = new Shuffle(List.of(), List.of(), new Random(2));
        List<String> before = new ArrayList<>();
        for (int i = 0; i < 15; i++) before.add(a.next(p).id);
        Shuffle b = new Shuffle(Shuffle.split(a.roundState()), Shuffle.split(a.historyState()), new Random(3));
        Set<String> after = new HashSet<>();
        for (int i = 0; i < 25; i++) after.add(b.next(p).id);
        Set<String> overlap = new HashSet<>(before); overlap.retainAll(after);
        check("after a restart the round carries on (no repeats of the first 15)", overlap.isEmpty() && after.size() == 25, "overlap " + overlap);
        check("counter after 40", b.shownThisRound(p) == 40, b.shownThisRound(p));

        // The remote's preview of Next: the same state and seed pick the same one, again and again.
        String round = b.roundState(), history = b.historyState();
        String peek = new Shuffle(Shuffle.split(round), Shuffle.split(history), new Random(77)).next(p).id;
        String again = new Shuffle(Shuffle.split(round), Shuffle.split(history), new Random(77)).next(p).id;
        String real = new Shuffle(Shuffle.split(round), Shuffle.split(history), new Random(77)).next(p).id;
        check("the preview of Next is the one Next shows", peek.equals(again) && peek.equals(real), peek + " " + real);

        // Projectivy's timing, from a real evening: it asks every 60 s, and any ask restarts that.
        check("Update delay: a minute until it's measured", Shuffle.base(List.of()) == 60_000, Shuffle.base(List.of()));
        check("Update delay: one odd gap (26 min in another app) doesn't move it",
            Shuffle.base(List.of("60000", "1560000", "60010", "59990", "60000")) == 60_000, Shuffle.base(List.of("60000", "1560000", "60010", "59990", "60000")));
        long t0 = 1_000_000_000L;   // a Next from the remote was shown at t0 (Projectivy asked then)
        check("every 1 minute: the next change at the next ask, 60 s on",
            Shuffle.nextChange(t0, 60_000, t0, 60_000, t0 + 5_000) == t0 + 60_000, Shuffle.nextChange(t0, 60_000, t0, 60_000, t0 + 5_000) - t0);
        check("every 5 minutes: the first ask after 4:45 (the 5th)",
            Shuffle.nextChange(t0, 60_000, t0, 300_000, t0 + 5_000) == t0 + 300_000, Shuffle.nextChange(t0, 60_000, t0, 300_000, t0 + 5_000) - t0);
        check("an ask that kept the wallpaper moves the grid with it",
            Shuffle.nextChange(t0 + 61_000, 60_000, t0, 300_000, t0 + 70_000) == t0 + 301_000, Shuffle.nextChange(t0 + 61_000, 60_000, t0, 300_000, t0 + 70_000) - t0);
        check("Projectivy asks every 2 min, change every minute: every 2 min",
            Shuffle.nextChange(t0, 120_000, t0, 60_000, t0 + 1_000) == t0 + 120_000, Shuffle.nextChange(t0, 120_000, t0, 60_000, t0 + 1_000) - t0);
        check("never in the past (an ask missed while asleep)",
            Shuffle.nextChange(t0, 60_000, t0, 60_000, t0 + 600_500) == t0 + 660_000, Shuffle.nextChange(t0, 60_000, t0, 60_000, t0 + 600_500) - t0);

        // The list changes: new ones join the current round, dropped ones don't block a new round.
        Shuffle c = new Shuffle(List.of(), List.of(), new Random(4));
        for (int i = 0; i < 39; i++) c.next(p);
        List<Shuffle.Item> grown = new ArrayList<>(p.subList(5, 40)); grown.addAll(pool(45).subList(40, 45));
        Set<String> next10 = new HashSet<>();
        for (int i = 0; i < 6; i++) next10.add(c.next(grown).id);
        check("a changed list: the new ones and the one left come first", next10.size() == 6, next10);

        check("an empty list gives nothing (no crash)", c.next(new ArrayList<>()) == null, "null");
        Shuffle d = new Shuffle(List.of(), List.of(), new Random(5));
        List<Shuffle.Item> one = pool(1);
        check("a list of one keeps showing it", "w0".equals(d.next(one).id) && "w0".equals(d.next(one).id), "w0");

        // TV settings: the list address must match the service's listQueryFor exactly (one shared cached list).
        TvSettings t = TvSettings.defaults();
        check("default list query = the one the plugin always used", t.listQuery().equals("range=1y&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=50&maxw=0"), t.listQuery());
        t.quality = "4k"; t.q = "  Night   CITY "; t.ratio = ""; t.clean();
        check("exact 4K, a topic, any shape", t.listQuery().equals("range=1y&res=3840x2160,3840x2400&cats=110&purity=100&q=night%20city&fav=50&maxw=0"), t.listQuery());
        TvSettings bad = new TvSettings();
        bad.quality = "9k"; bad.ratio = "3x1"; bad.range = "2y"; bad.cats = "000"; bad.fav = -3; bad.maxw = 1234; bad.every = 0; bad.q = "<script>";
        bad.sources.addAll(List.of("toplist", "toplist", "fav", "p_ab12cd", "evil", "p_X"));
        bad.clean();
        check("clean() puts anything odd back to a valid value", bad.quality.equals("4k+") && bad.ratio.equals("16x9,16x10") && bad.range.equals("1y") && bad.cats.equals("110")
            && bad.fav == 50 && bad.maxw == 3840 && bad.every == 1 && bad.q.isEmpty() && bad.sources.equals(List.of("toplist", "fav", "p_ab12cd")), bad.sources);
        check("labels", TvSettings.everyLabel(1).equals("1 minute") && TvSettings.everyLabel(120).equals("2 hours") && TvSettings.everyLabel(1440).equals("1 day")
            && TvSettings.favLabel(1000).equals("1,000+ favourites") && new TvSettings().catsLabel().equals("General, Anime"), TvSettings.everyLabel(45));
        check("a typed favourites minimum", TvSettings.parseFav("75") == 75 && TvSettings.parseFav(" 1,500 ") == 1500 && TvSettings.parseFav("0") == 0 && TvSettings.parseFav("100000") == 100000
            && TvSettings.parseFav("100001") == -1 && TvSettings.parseFav("-5") == -1 && TvSettings.parseFav("7.5") == -1 && TvSettings.parseFav("") == -1, TvSettings.parseFav(" 1,500 "));

        // Popularity ranges: the same words and rules as the remote and the PC
        check("range labels", TvSettings.favLabel(0, 0).equals("Any") && TvSettings.favLabel(250, 0).equals("250+ favourites")
            && TvSettings.favLabel(0, 500).equals("Up to 500 favourites") && TvSettings.favLabel(250, 1000).equals("250 to 1,000 favourites"), TvSettings.favLabel(250, 1000));
        String[] err = new String[1];
        check("200 to 1,000", Arrays.equals(TvSettings.parseRange("200", "1,000", err), new int[]{200, 1000}) && err[0] == null, err[0]);
        check("an empty At least is 0, an empty At most no limit", Arrays.equals(TvSettings.parseRange("", "300", err), new int[]{0, 300})
            && Arrays.equals(TvSettings.parseRange("75", " ", err), new int[]{75, 0}) && Arrays.equals(TvSettings.parseRange("500", "500", err), new int[]{500, 500}), "");
        check("At most below At least says why", TvSettings.parseRange("500", "200", err) == null && "At most: 500 or more, or empty".equals(err[0]), err[0]);
        check("At most 0 says why", TvSettings.parseRange("", "0", err) == null && "At most: 1 or more, or empty".equals(err[0]), err[0]);
        check("bad numbers say which field", TvSettings.parseRange("abc", "", err) == null && err[0].startsWith("At least:")
            && TvSettings.parseRange("1", "200000", err) == null && err[0].startsWith("At most:"), err[0]);
        // Wallhaven sorted by favourites (Most favourited): its own range and order; the other filters are shared
        TvSettings r = TvSettings.defaults();
        r.fav = 200; r.mfFav = 200; r.mfFavmax = 1000; r.mfOrder = "shuffle"; r.clean();
        check("the toplist's list query: a minimum only", r.listQuery().equals("range=1y&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=200&maxw=0"), r.listQuery());
        check("Most favourited's query, the service's mostfavQueryFor", r.mostfavQuery().equals("sort=fav&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=200&favmax=1000&maxw=0"), r.mostfavQuery());
        check("no upper limit: no favmax in it", TvSettings.defaults().mostfavQuery().equals("sort=fav&atleast=3840x2160&ratios=16x9,16x10&cats=110&purity=100&fav=250&maxw=0"), TvSettings.defaults().mostfavQuery());
        check("sorted as the toplist by default: its list is the toplist's", r.sort.equals("toplist") && r.activeQuery().equals(r.listQuery()) && !r.mostfavOn(), r.sort);
        TvSettings mig = TvSettings.defaults(); mig.sources.clear(); mig.sources.add("mostfav"); mig.sources.add("fav"); mig.clean();
        check("Most favourited as a source (1.21): Wallhaven sorted by favourites", mig.sources.equals(List.of("toplist", "fav")) && mig.sort.equals("fav") && mig.mostfavOn(), mig.sources + " " + mig.sort);
        TvSettings both = TvSettings.defaults(); both.sources.add("mostfav"); both.clean();
        check("both on: Wallhaven once, by favourites", both.sources.equals(List.of("toplist")) && both.sort.equals("fav"), both.sources.toString());
        TvSettings badSort = TvSettings.defaults(); badSort.sort = "newest"; badSort.clean();
        check("an unknown sort: the toplist", badSort.sort.equals("toplist"), badSort.sort);
        check("by favourites, its list is Most favourited's", mig.activeQuery().equals(mig.mostfavQuery()), mig.activeQuery());
        r.sort = "fav";
        TvSettings odd = TvSettings.defaults();
        odd.mfFav = 2000; odd.mfFavmax = 1000; odd.mfOrder = "fav"; odd.clean();
        TvSettings eq = TvSettings.defaults(); eq.mfFav = 500; eq.mfFavmax = 500; eq.clean();
        check("a maximum below the minimum: no upper limit (equal is fine); an unknown order: Most first", odd.mfFavmax == 0 && odd.mfOrder.equals("top") && eq.mfFavmax == 500, odd.mfFavmax + " " + odd.mfOrder);
        TvSettings cp = r.copy();
        check("copy keeps the sort, the range and the order", cp.sort.equals("fav") && cp.mfFav == 200 && cp.mfFavmax == 1000 && cp.mfOrder.equals("shuffle"), cp.mfOrder);
        check("Wallhaven in a line, the remote's and PC's words", r.wallhavenSummary().equals("Most favourited\u00a0· 200 to 1,000\u00a0· Shuffled")
            && TvSettings.defaults().wallhavenSummary().equals("Toplist\u00a0· Past year"), r.wallhavenSummary());
        TvSettings any = r.copy(); any.mfFav = 0; any.mfFavmax = 0; any.mfOrder = "top"; any.q = "nature";
        check("any range: just the sort, and a topic", any.wallhavenSummary().equals("Most favourited\u00a0· “nature”"), any.wallhavenSummary());
        r.mfOrder = "top";
        check("by favourites, Most first: the pool plays by favourites", r.mostFirst(), "");
        TvSettings off = r.copy(); off.sources.clear(); off.sources.add("fav");
        TvSettings top = r.copy(); top.sort = "toplist";
        check("Most first only while Wallhaven is on and sorted by favourites", !off.mostFirst() && !top.mostFirst() && !TvSettings.defaults().mostFirst(), "");
        Shuffle.Item big = new Shuffle.Item("big", "i", "p", "3840x2160", 3840, 1500), small = new Shuffle.Item("small", "i", "p", "3840x2160", 3840, 300),
            wide = new Shuffle.Item("wide", "i", "p", "5120x2880", 5120, 300), few = new Shuffle.Item("few", "i", "p", "3840x2160", 3840, 150);
        check("the toplist's limit is the width only", r.inLimits(big) && r.inLimits(small) && !r.inLimits(wide), "");
        check("Most favourited's range, applied again on the TV", !r.inMostfav(big) && r.inMostfav(small) && !r.inMostfav(wide) && !r.inMostfav(few), "");

        // Most first: the most favourited one not shown this round, round after round; ties in the pool's order.
        List<Shuffle.Item> ranked = new ArrayList<>();
        int[] favs = {10, 900, 50, 900, 0};
        for (int i = 0; i < favs.length; i++) ranked.add(new Shuffle.Item("abcde".substring(i, i + 1), "i", "p", "3840x2160", 3840, favs[i]));
        Shuffle byFav = new Shuffle(List.of(), List.of(), new Random(3));
        StringBuilder seq = new StringBuilder();
        for (int i = 0; i < 7; i++) seq.append(byFav.next(ranked, true).id);
        check("most favourited first, then a new round from the top", seq.toString().equals("bdcaebd"), seq);
        Set<String> firsts = new HashSet<>();
        for (int i = 0; i < 20; i++) firsts.add(new Shuffle(List.of(), List.of(), new Random(i + 1)).next(ranked, false).id);
        check("shuffled picks vary", firsts.size() > 2, firsts);

        System.out.println(fail == 0 ? "ALL PASSED" : fail + " FAILED");
        System.exit(fail == 0 ? 0 : 1);
    }
}
