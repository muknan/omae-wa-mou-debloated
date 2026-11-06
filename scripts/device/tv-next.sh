#!/system/bin/sh
# tv-next.sh - "next episode" for one remote button, in every app. Run it from a Key Mapper key map
# (Execute with ADB: sh /data/local/tmp/tv-next.sh).
#
# Most players take Android's media Next (SmartTube, Spotify, mpv...). Nuvio doesn't: its player tells Android it
# can't skip to next, and uses the Next / Channel Up keys to skip chapters. Its only way to the next episode is the
# player's own "Next episode" button, right of Play/Pause. So for Nuvio this presses Up, Up, Down, Right, OK, which
# lands on that button whatever state the player is in (tested: controls hidden, controls open on Play/Pause or
# another button, paused). Only while a series episode plays (the media info says S<n>:E<n>), so it never opens a
# menu on a movie. Paused first? Play, which also closes Nuvio's pause screen.
NUVIO=com.nuvio.tv
top=$(dumpsys activity activities 2>/dev/null | grep -m1 -E 'topResumedActivity|mResumedActivity' | sed 's/.* u0 //; s/\/.*//')

if [ "$top" = "$NUVIO" ]; then
    s=$(dumpsys media_session 2>/dev/null | grep -A12 "package=$NUVIO" | grep -E 'state=PlaybackState|description=')
    case "$s" in *S[0-9]*:E[0-9]*) ;; *) exit 0 ;; esac          # not an episode: nothing to skip to
    case "$s" in *"state=PAUSED"*|*"state=2"*) input keyevent 126; sleep 0.5 ;; esac
    for k in 19 19 20 22; do input keyevent $k; sleep 0.35; done   # Up, Up, Down, Right: onto "Next episode"
    input keyevent 23
    exit 0
fi

# Everyone else: the media Next key (the app on screen first, then the active media session).
input keyevent 87
