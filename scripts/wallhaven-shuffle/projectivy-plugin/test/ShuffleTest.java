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
        check("labels", TvSettings.everyLabel(1).equals("Every minute") && TvSettings.everyLabel(120).equals("Every 2 hours") && TvSettings.everyLabel(1440).equals("Once a day")
            && TvSettings.favLabel(1000).equals("1,000+ favourites") && new TvSettings().catsLabel().equals("General, Anime"), TvSettings.everyLabel(45));

        System.out.println(fail == 0 ? "ALL PASSED" : fail + " FAILED");
        System.exit(fail == 0 ? 0 : 1);
    }
}
