package io.github.muknan.wallhavenshuffle;

import java.util.ArrayList;
import java.util.Collection;
import java.util.HashSet;
import java.util.List;
import java.util.Random;
import java.util.Set;

/**
 * What comes next. Every wallpaper in the pool is shown once per round, in random order, and where possible never
 * one of the last few shown, so the end of one round and the start of the next don't repeat either. The same rules
 * as the Windows app. Plain Java (no Android), so it's tested on the PC; the state is two strings the service keeps
 * in its preferences.
 */
final class Shuffle {
    static final int HISTORY = 50;

    /** One wallpaper of the list. */
    static final class Item {
        final String id, image, page, resolution;
        final int width, favorites;

        Item(String id, String image, String page, String resolution, int width, int favorites) {
            this.id = id; this.image = image; this.page = page; this.resolution = resolution;
            this.width = width; this.favorites = favorites;
        }
    }

    private final List<String> round = new ArrayList<>();      // shown this round, in order
    private final Set<String> inRound = new HashSet<>();
    private final List<String> history = new ArrayList<>();    // shown, newest last (the last HISTORY)
    private final Random rng;

    Shuffle(Collection<String> round, Collection<String> history, Random rng) {
        for (String id : round) if (!id.isEmpty() && inRound.add(id)) this.round.add(id);
        for (String id : history) if (!id.isEmpty()) this.history.add(id);
        this.rng = rng;
    }

    /** The next one, recorded as shown; null for an empty pool. */
    Item next(List<Item> pool) {
        if (pool.isEmpty()) return null;
        boolean allShown = true;
        for (Item w : pool) if (!inRound.contains(w.id)) { allShown = false; break; }
        if (allShown) { round.clear(); inRound.clear(); }                 // every one was shown: a new round

        int avoid = Math.min(20, pool.size() / 2);
        Set<String> recent = new HashSet<>(history.subList(Math.max(0, history.size() - avoid), history.size()));
        List<Item> fresh = new ArrayList<>(), notRecent = new ArrayList<>();
        for (Item w : pool) {
            if (inRound.contains(w.id)) continue;
            fresh.add(w);
            if (!recent.contains(w.id)) notRecent.add(w);
        }
        List<Item> from = notRecent.isEmpty() ? fresh : notRecent;
        Item pick = from.get(rng.nextInt(from.size()));

        round.add(pick.id); inRound.add(pick.id);
        history.add(pick.id);
        if (history.size() > HISTORY) history.subList(0, history.size() - HISTORY).clear();
        return pick;
    }

    /** How many of the pool were shown this round (the "12" in "12 of 677"). */
    int shownThisRound(List<Item> pool) {
        int n = 0;
        for (Item w : pool) if (inRound.contains(w.id)) n++;
        return n;
    }

    String roundState() { return String.join(",", round); }
    String historyState() { return String.join(",", history); }

    // ---- Projectivy's timing (plain Java, so it's tested off the TV)

    /** Projectivy's Update delay, from the gaps between its own asks: the middle one (an odd gap, say after the TV
     *  slept, doesn't move it), or a minute until there are any. */
    static long base(List<String> gaps) {
        List<Long> g = new ArrayList<>();
        for (String x : gaps) try { g.add(Long.parseLong(x)); } catch (NumberFormatException ignored) { }
        if (g.isEmpty()) return 60_000;
        java.util.Collections.sort(g);
        return g.get(g.size() / 2);
    }

    /** When the next change comes. Projectivy asks every `base` after its last ask (any ask restarts its timer), and
     *  the change happens at the first ask once `every` has passed since the last change (15 s early is fine). */
    static long nextChange(long lastAsk, long base, long changed, long every, long now) {
        long due = changed + every - 15_000;
        long t = lastAsk + base;
        if (t < due) t += ((due - t + base - 1) / base) * base;
        if (t < now) t += ((now - t + base - 1) / base) * base;   // an ask it missed (asleep): the next one from now
        return t;
    }

    static List<String> split(String s) {
        List<String> out = new ArrayList<>();
        if (s == null || s.isEmpty()) return out;
        for (String id : s.split(",")) if (!id.isEmpty()) out.add(id);
        return out;
    }
}
