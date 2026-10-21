# 9. Quality-of-life extras

> **Easier:** the wizard's step 2 sets up the daily refresh, and its extras step offers the ad-blocking DNS and
> the YouTube-links switch. This page explains each one and how to do or undo it by hand.

All of these were tested on the Xiaomi test TV. Each has a one-line undo.

## Daily refresh

Once a day the TV does what the [refresh button](05-key-mapper.md) does: every background app goes back to sleep
and cached apps are cleared. You never have to remember to press it.

- `scripts/device/tv-daily.sh` is a tiny shell script (about 3 MB of memory) started by `tv-apply.sh`, the same
  way Key Mapper's Expert Mode runs. It survives standby and is restarted by the boot helper after a restart.
- It checks every 15 minutes and refreshes on the first check at or after 4:00 each day. When the TV is in
  standby it's asleep too, so in practice the refresh happens within 15 minutes of turning the TV on.
- **It never interrupts:** if anything is playing (music, video), it waits for the next check.
- Settings in `tv-apply.conf` (re-read at every check, no restart needed):

  ```sh
  DAILY_REFRESH="1"        # 0 = off; the script then stops by itself
  DAILY_REFRESH_HOUR="4"   # earliest hour of the day to refresh
  ```

- See what it did: `adb shell tail /data/local/tmp/tv-apply.log` shows a `daily refresh` line each day.
- Stop it now: `adb shell 'kill $(cat /data/local/tmp/tv-daily.pid)'`. Undo everything removes it too.

## Ad and tracker blocking for the whole TV (Private DNS)

Android's Private DNS can point at [AdGuard DNS](https://adguard-dns.io/en/public-dns.html), which answers ad and
tracking domains with nothing. That covers every app, the home screen's remaining ad rows and the TV's own
trackers.

```powershell
adb shell settings put global private_dns_specifier dns.adguard-dns.com
adb shell settings put global private_dns_mode hostname
```

- Tested on the test TV: Netflix, Prime Video, YouTube (SmartTube), JioHotstar, Twitch, Nuvio and the Play Store
  all load normally; `doubleclick.net` is blocked, normal sites resolve.
- It can't block ads that come from the same servers as the video (YouTube's own ads, some in-app banners).
- Trade-offs: AdGuard sees which domains the TV looks up, and if their service is down the TV shows "no internet"
  until you switch it off. A rare app may refuse to work; switch it off to check.
- Undo: `adb shell settings put global private_dns_mode opportunistic`

## Open YouTube links in SmartTube

If you use [SmartTube](https://github.com/yuliskov/SmartTube) (ad-free YouTube for TVs), YouTube links from
search, Watch Next rows or other apps can open there directly instead of asking "open with?" every time:

```powershell
adb shell pm set-app-links-user-selection --user 0 --package org.smarttube.stable true youtube.com www.youtube.com m.youtube.com youtu.be
adb shell pm set-app-links-allowed --user 0 --package com.google.android.youtube.tv false
```

The official YouTube app still opens from its icon and the remote's YouTube button, and casting from the YouTube
phone app still uses it. Undo: run both lines again with `false` and `true` swapped.

## Wallpapers: top of the last month, in your TV's resolution

See [06](06-launcher-and-wallpapers.md#rotating-wallpapers-from-wallhaven). `topRange=1M` gives a fresher set
than `6M`; Projectivy re-asks every so often, so the list follows Wallhaven's toplist as it changes.

## Back up your launcher

Projectivy: **Settings > Manage settings > Export > Share with another app > File Manager + > Downloads > Save**.
Copy the `.plbackup` file to your PC (`adb pull /sdcard/Download/<file>`). Restore with **Import**.

Key Mapper can only save backups through Android's "Save as" screen, which many Google TVs don't have. If
**Back up everything** in Key Mapper shows nothing, write your key maps down (trigger + action); rebuilding them
takes a few minutes. Restoring a Key Mapper backup file works with File Manager+ as the picker.

## Give the TV a fixed IP address

Your router hands out addresses, and they can change (the test TV moved from `192.168.0.232` to `192.168.1.13`
overnight). The boot helper doesn't care, but the wizard and any PC commands do. In your router's admin page
(usually `http://192.168.1.1` or `http://192.168.0.1`), find **DHCP reservation** / **Static lease** / **Address
reservation** and reserve the TV's current IP for its MAC address:

```powershell
adb shell "dumpsys wifi | grep -m1 -oE 'MAC: [0-9a-f:]{17}'"
```

Android TVs use a "private" MAC per Wi-Fi network by default. It stays the same for your network, so reserving
it works; it only changes if you forget the network or reset the TV.

## Refresh-rate matching

Android's "match content frame rate", Projectivy's display profiles and SmartTube's auto frame rate all switch the
TV to a 24/50 Hz mode for films. They only work if the TV offers those modes to Android. Check yours:

```powershell
adb shell "dumpsys display | grep -m1 supportedModes"
```

The test TV lists only `3840x2160 @ 60`, so there's nothing to switch to: its own video chip handles film cadence.
On TVs and boxes that list several rates, turn on **Settings > Display & Sound > Advanced display settings >
Match content frame rate** and SmartTube's **auto frame rate**.
