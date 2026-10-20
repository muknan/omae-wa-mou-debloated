# omae-wa-mou-debloated

**Other debloats clean your TV once. This one keeps it clean.**

Make a slow Google TV fast again, and keep it that way after every restart. No rooting, nothing deleted,
and you can undo all of it. One double-click, a few questions, done.

*Omae wa mou debloated.* Your apps are already asleep. Even after a reboot.

<p align="center"><img src="screenshots/hero.jpg" width="100%" /></p>
<p align="center"><sub>One TV, one home screen, a new wallpaper from Wallhaven every so often. No ads, no "top picks for you".</sub></p>

## What is this?

Google TV and Android TV sets ship with dozens of apps you never asked for: ad rows, viewing trackers, shop demo
modes, silent app installers. On top of that, every app you install keeps waking up in the background. On a TV
with 2 GB of memory, that's the lag you feel.

This kit fixes both, and, unlike most debloat guides, keeps it fixed after the TV restarts.

## What it does

- **Switches off the junk** your TV came with: ads, tracking, shop demo mode, silent app installers. Switched off,
  not deleted, so everything can come back.
- **Keeps apps asleep** when you're not using them, including apps you install later. They open normally when
  you pick them; they just stop running in the background.
- **Makes menus feel snappier** (shorter animations).
- **Survives restarts** (optional): a small helper app on the TV re-applies everything at every startup, no PC needed.
- **Adds a "fresh boot" button** to your remote (optional): press a spare button twice and every background app
  goes to sleep, like a restart without the wait.
- **Gives you an ad-free home screen** with rotating wallpapers (optional).
- **Undo everything** with one click.

## Why this one?

Most TV debloat guides switch off some apps, add a new home screen, and stop there.
Then the TV restarts and quietly undoes half of it: apps wake back up, menus slow down again, and the debugging connection the tweaks rely on switches itself off.

| | Typical debloat | This kit |
|---|---|---|
| Switches off junk apps | ✅ | ✅ with a risk level for every package, per brand |
| Keeps apps asleep in the background | ❌ | ✅ every app, even new installs |
| Still working after a restart | half of it | ✅ the TV re-applies everything itself |
| The debugging connection comes back by itself after a restart | ❌ | ✅ with ADB Auto Enable |
| A remote button that clears background apps without restarting | ❌ | ✅ with Key Mapper |
| Checks the TV after every change and rolls back if something breaks | ❌ | ✅ |
| No leftover "running" notifications from the helpers | n/a | ✅ hidden automatically |

## Which TVs?

| TV | Works? |
|---|---|
| **Google TV** (Xiaomi, TCL, Sony, Philips, Hisense, Haier, onn, Chromecast, Google TV Streamer...) | ✅ |
| **Android TV** (older Sony, Philips, TCL, Hisense, NVIDIA Shield...) | ✅ Android 9 or newer for everything; older ones get the junk-app step only |
| **Amazon Fire TV** | ✅ junk apps, sleep, boot helper. No custom home screen (Fire OS blocks it) |
| Samsung, LG, Roku, Hisense VIDAA | ❌ not Android |

Built and tested on a Xiaomi TV X Pro 2025 (Google TV 14, 2 GB): full runs, restart tests, and a real Undo and
re-run. The catalog also covers packages from community-tested lists for 90+ models from more than 15 brands.
Packages it doesn't know are left alone.

---

## Setup (about 20 minutes)

You need a **Windows PC** on the **same Wi-Fi** as the TV, and the TV remote.

**1. Download.** Click the green **Code** button above > **Download ZIP**. Right-click the ZIP > **Extract All**.

**2. Turn on debugging on the TV** (one time):
Settings > System > About > press OK on **Android TV OS build** 7 times ("You are now a developer").
Then Settings > System > **Developer options** > turn on **USB debugging** (and **Wireless debugging** /
**Network debugging** if you see it). The wizard shows the steps for Fire TV and Shield too.

**3. Double-click `Start-Here.cmd`** in the extracted folder and answer the questions.

That's it. The wizard finds your TV on the network, tells you whenever you need to press something on the TV,
and does the rest. This is a real run:

<p align="center"><img src="screenshots/wizard-debloat.jpg" width="90%" /></p>

> Windows says "Windows protected your PC"? Click **More info** > **Run anyway**. It's a plain text script;
> open it in Notepad if you want to see what it does.

### Using an AI assistant instead?

Open this folder in Claude Code, Codex, Cursor or similar and say:
**"Read AI-SETUP.md and set up my TV."** It asks everything in one go, shows you the risk level of every
change, and drives the same wizard. Details: [AI-SETUP.md](AI-SETUP.md).

## What the wizard asks

Only the questions that matter for **your** TV are asked. Not sure? Press Enter for the safe suggestion.

| Question | Suggested |
|---|---|
| The brand's own channels, content tabs, sponsored rows? | switch off (these are the ads) |
| Antenna / satellite / cable plugged into the TV? | keep |
| AirPlay from Apple devices? | keep |
| Chromecast / Cast button? | keep |
| Screen mirroring (Miracast, brand cast app)? | keep |
| Alexa on the TV? | switch off |
| Photo/art screensaver? | switch off on 2-3 GB TVs, keep on bigger ones |
| USB-stick playback with the TV's own player? | keep |
| Brand smart-home / phone app? | keep |
| Preinstalled apps you never use | you pick, default none |
| Which apps may keep running (music, VPN)? | detected automatically, you can change it |

## See it work

**It survives a restart.** The "boot helper" is a tiny script that runs on the TV itself (inside the free
Termux app) every time the TV starts: it re-applies your settings, puts background apps back to sleep, and closes
itself. At the end of setup the wizard restarts the TV to prove it works with no PC. Below: that restart test, and
the helper's own log on the TV (one entry per restart).

<p align="center">
  <img src="screenshots/wizard-restart.jpg" width="49%" />
  <img src="screenshots/boot-helper-log.jpg" width="49%" />
</p>

**The fresh-boot button.** [Key Mapper](https://github.com/keymapperorg/KeyMapper) is a free, open-source app that
lets you change what your remote's buttons do. The wizard installs it and walks you through one key map: press a button you
never use (a colour button, for example) **twice quickly**, and it runs the same cleanup the TV does at startup.
Every background app goes to sleep and memory is freed, without restarting. The volume bar flashes when it's done, so you know it
worked. Spare buttons can also open your favourite apps directly.

<p align="center">
  <img src="screenshots/keymapper-refresh-button.jpg" width="49%" />
  <img src="screenshots/keymapper-keymaps.jpg" width="49%" />
</p>

**The home screen.** [Projectivy Launcher](https://play.google.com/store/apps/details?id=com.spocky.projengmenu)
is a free home-screen app that replaces the ad-filled one. With the
[Booru Wallpaper Provider](https://github.com/Kurocon/projectivy-booru-wallpaper-provider) plugin pointed at
[Wallhaven](https://wallhaven.cc), it shows a new wallpaper every so often. Your Netflix, YouTube and other apps
stay exactly where you put them.

<p align="center">
  <img src="screenshots/launcher-quick-menu.jpg" width="49%" />
  <img src="screenshots/launcher-all-apps.jpg" width="49%" />
  <img src="screenshots/launcher-wallpaper-settings.jpg" width="49%" />
  <img src="screenshots/wallhaven-plugin.jpg" width="49%" />
</p>

<details>
<summary><b>More wallpapers, full size</b></summary>
<p align="center">
  <img src="screenshots/home-01.jpg" width="49%" />
  <img src="screenshots/home-02.jpg" width="49%" />
  <img src="screenshots/home-06.jpg" width="49%" />
  <img src="screenshots/home-07.jpg" width="49%" />
  <img src="screenshots/home-09.jpg" width="49%" />
  <img src="screenshots/home-10.jpg" width="49%" />
</p>
</details>

## How it keeps you safe

- **Nothing is deleted.** Apps are switched off (`pm disable-user`), never uninstalled. No root.
- **Every built-in app has a risk level** in the [catalog](catalog/): *low* (junk, switched off), *medium* (only if
  you don't use that feature), *keep* (never touched: inputs, remote, updates, casting, setup, accessibility).
  If any tested TV needed an app, it's *keep*.
- **Apps it doesn't recognise are left alone.**
- **3 at a time, checked.** After each batch the wizard presses Home and checks that Android didn't restart,
  Home still works and no setup/recovery screen appeared. If anything's off, that batch is rolled back
  automatically and skipped from then on. You can also press **U** to undo a batch yourself.
- **Everything is logged** in `my-tv\<your TV>\`, with a snapshot of how it was before.
- Helper apps come from their official GitHub releases and are installed only if their SHA-256 matches.

## Undo everything

Double-click `Start-Here.cmd` > **Undo everything**. It switches everything back on, removes the background
limits, the scripts and the accessibility helpers it added, restores the animation speed, and offers to
uninstall the helper apps it installed. Then the TV restarts.

## Stuck?

| Problem | Fix |
|---|---|
| The wizard doesn't find the TV | Debugging switched itself off (it does when the TV loses Wi-Fi) or the PC is on a VPN. Turn it back on, choose *Search again* or type the TV's IP. |
| No "Allow USB debugging?" box | Look at the TV while it connects; the box can hide behind an app. Press Home and retry. |
| It asks for a pairing code | Newer TVs need one: Developer options > Wireless debugging > *Pair device with pairing code*. |
| The test restart says the helper didn't run | See [Troubleshooting > Boot helper](docs/08-troubleshooting.md#boot-helper). |
| Something stopped working | **Undo everything**, or switch back just that one app: see [Troubleshooting](docs/08-troubleshooting.md). |

---

<details>
<summary><b>For the curious: results, how it works, tools, manual mode</b></summary>

### Results on the test TV (Xiaomi TV X Pro 2025, 2 GB RAM)

| | Before | After |
|---|---|---|
| Free memory after a restart | 530 MB | 612 MB |
| Apps running after a restart | 40 | 33 |
| Apps that start by themselves | running | asleep |
| AirPlay service | stuck at 100% of a CPU core | off |

### How it works

- Junk apps are **disabled, not deleted**, so they come back with one command.
- Android forgets "keep this app asleep" every time the TV restarts. **Termux + Termux:Boot** run a small script
  at every startup that connects to the TV's own ADB, re-applies everything, puts background apps to sleep
  and then closes itself.
- Android 14+ turns wireless debugging off after a restart or Wi-Fi drop. **ADB Auto Enable** turns it back on,
  so the boot helper always gets in.
- The remote button is a **Key Mapper** key map that runs the same script and flashes the volume bar when done.
- The helpers' permanent "running" notifications are snoozed by the same script, so no badge shows up on your
  home screen.
- Every tweak was tested on a real TV. Some popular tips made things worse and were left out: see
  [what we measured](docs/07-measuring.md).

### Tools used

| Tool | What for |
|---|---|
| [Android Platform-Tools](https://developer.android.com/tools/releases/platform-tools) | lets the PC talk to the TV |
| [Termux](https://github.com/termux/termux-app) + [Termux:Boot](https://github.com/termux/termux-boot) | re-applies settings after a restart |
| [ADB Auto Enable](https://github.com/mouldybread/adb-auto-enable) | turns wireless debugging back on after a restart |
| [Key Mapper](https://github.com/keymapperorg/KeyMapper) | remote button tricks |
| [Projectivy Launcher](https://play.google.com/store/apps/details?id=com.spocky.projengmenu) | ad-free home screen |
| [Booru Wallpaper Provider](https://github.com/Kurocon/projectivy-booru-wallpaper-provider) | rotating wallpapers from [Wallhaven](https://wallhaven.cc) |

### Wizard options (also for AI assistants and scripting)

```powershell
.\setup\Wizard.ps1 -Device 192.168.1.50 -Plan        # detect + plan, change nothing
.\setup\Wizard.ps1 -Device 192.168.1.50 -Answers answers.json -Auto
.\setup\Wizard.ps1 -Device 192.168.1.50 -Steps background,stick
.\setup\Wizard.ps1 -Device 192.168.1.50 -Status
.\setup\Wizard.ps1 -Device 192.168.1.50 -Undo
.\tests\DryRun.ps1 -Device 192.168.1.50              # run the steps with every change printed instead of done
```

### Manual mode and all guides

Prefer doing it by hand? The step-by-step guides and the original scripts (`scripts/pc/`) are still here:
[Connecting](docs/01-adb-setup.md) · [Junk apps](docs/02-audit-and-debloat.md) · [Settings tweaks](docs/03-settings-tweaks.md) · [Make it stick](docs/04-boot-agent.md) · [Remote buttons](docs/05-key-mapper.md) · [Launcher & wallpapers](docs/06-launcher-and-wallpapers.md) · [Measuring](docs/07-measuring.md) · [Troubleshooting](docs/08-troubleshooting.md)

</details>

## Good to know

- Keeping wireless debugging on means devices on your Wi-Fi *could* ask to connect, but only ones you've approved
  get in. Use it on your home Wi-Fi only.
- Package knowledge for other brands builds on the community-tested lists in
  [Gathin23/cleanyourtv](https://github.com/Gathin23/cleanyourtv) (MIT). Thanks!
- Not affiliated with Xiaomi, Google or any app above. MIT license, use at your own risk.
