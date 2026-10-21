# AI setup playbook

This file is for an AI assistant with a terminal (Claude Code, Codex, Cursor, Gemini CLI, ...).
The human just says: **"Read AI-SETUP.md and set up my TV."** Everything you need is in this repo.

Your job: get the same result as the double-click wizard, with as little back-and-forth as possible.
Ask everything up front in **one** message, with a recommended answer for each question, then do the work
and only come back when the human has to press something on the TV.

---

## 0. Ground rules (non-negotiable)

- Never root, factory reset, flash firmware or **uninstall** system packages. Only `pm disable-user --user 0`
  (reversible), and only through the wizard or exactly as described below.
- Never touch packages with risk `keep` in [`catalog/packages.csv`](catalog/packages.csv). Never touch packages
  that are not in the catalog without the human's explicit OK for that specific package (see section 7).
- Never change the HOME role or default launcher setting, resolution, refresh rate, DPI, picture or sound
  settings. Never use "background process limit", "Don't keep activities", task killers or bulk data clearing.
- Switch things off in batches of at most 3, and check the TV after each batch (the wizard does this for you
  and rolls a batch back automatically if Home, the launcher or Android itself breaks).
- Before any step that changes the TV, show the human what will change and get a yes. One yes covers one step.
- If something is unsupported or unclear, stop and say so. Don't try a more aggressive alternative.
- Downloaded APKs must match the SHA-256 in [`catalog/helpers.json`](catalog/helpers.json). The wizard checks
  this; don't install helper apps from anywhere else.
- Don't revoke apps' notification permission to silence them: revoking restarts the app (Key Mapper then posts a
  "needs restarting" warning) and some firmwares grant it back at boot. The boot script hides helper notifications
  by snoozing them instead.
- Don't restart the TV within ~15 seconds of switching packages on or off: Android saves those changes to disk
  with a delay, and an immediate restart can lose them. The wizard already waits; do the same by hand.

## 1. Tools you have

| Tool | What for |
|---|---|
| `setup/Wizard.ps1 -Device <ip> -Plan` | Connects, detects the TV and writes `my-tv/<tv>/plan.json` + `answers-template.json`. **Changes nothing.** |
| `setup/Wizard.ps1 -Device <ip> -Answers <file> -Auto [-Steps ...]` | Runs the setup with no questions, using the answers file. |
| `setup/Wizard.ps1 -Device <ip> -PairCode <code> [-PairPort <port>]` | Pairs ADB Auto Enable (Android 14+), see step 5. |
| `setup/Wizard.ps1 -Device <ip> -Status` / `-Undo -Auto` | Health check (boot helper ran this boot, Expert Mode, background helper, helper notifications, Private DNS, Wi-Fi reconnects) / undo everything. Run `-Status` after a restart to confirm everything came back. |
| `tests/DryRun.ps1 -Device <ip> -Steps debloat,background -Answers <file> -Auto` | Same as the real run, but every change is printed instead of done. Use it to preview. |
| `adb` | Read-only checks (`adb -s <ip>:5555 shell ...`). Use `shell -n` so adb doesn't eat your stdin, and add `2>/dev/null` after `dumpsys` when piping it into `grep -m1` (otherwise "Broken pipe" errors end up in your output). |

On Windows run PowerShell scripts as `powershell -NoProfile -ExecutionPolicy Bypass -File <script> <args>`.
On macOS/Linux use PowerShell 7 (`pwsh`); this path is less tested, so preview with `tests/DryRun.ps1` first.
If `adb` is missing, the wizard offers to download Google's platform-tools into `tools/`; in `-Auto` mode,
download it first (`winget install --id Google.PlatformTools -e` on Windows, `brew install android-platform-tools`
on macOS).

## 2. First message to the human (one message, everything up front)

Send something like this, filling in recommendations. Keep it friendly and short:

> Before I start, a few quick questions. Just reply with the numbers that differ from my suggestion,
> or "all good".
>
> **On the TV first:** Settings > System > About > press OK on "Android TV OS build" 7 times, then
> Settings > System > Developer options > turn on **USB debugging** (and **Wireless/Network debugging** if
> you see it). Tell me the TV's IP address (Settings > Network > your Wi-Fi). Fire TV: Settings > My Fire TV >
> About > press OK on the device name 7 times, then Developer options > ADB debugging.
>
> 1. Do you watch channels through an antenna, satellite or cable plugged straight into the TV? *(suggest: not sure = yes)*
> 2. Do you cast from an iPhone/iPad/Mac (AirPlay)? *(suggest: yes if anyone at home has Apple devices)*
> 3. Do you use Chromecast / the Cast button? *(suggest: yes)*
> 4. Do you mirror a phone/PC screen (Miracast, the brand's cast app)? *(suggest: yes if unsure)*
> 5. Do you use the TV brand's own channels, content tabs or app store? *(suggest: no, these are the ads)*
> 6. Alexa on the TV? *(suggest: no)*  7. Photo/art screensaver? *(suggest: no on 2 GB TVs, it uses ~100 MB)*
> 8. USB-stick playback with the TV's own player? *(suggest: yes if unsure)*
> 9. Which apps should keep running in the background after you start them (music, VPN, cast receivers)?
>    *(I'll detect music apps and VPNs automatically)*
> 10. Extras: survive restarts (recommended), a "fresh boot" button on the remote (Key Mapper), an ad-free home
>     screen with rotating wallpapers (Projectivy), a daily automatic refresh (recommended), ad-blocking DNS for
>     every app (AdGuard; off by default, see the trade-offs in docs/09), YouTube links in SmartTube?
> 11. Menu animations: normal, faster (suggested) or off?

Skip questions that don't apply once you have the plan (the plan only lists questions for features the TV
actually has). If the human already told you some of this, don't ask again.

## 3. Connect and plan (no changes)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File setup\Wizard.ps1 -Device <ip> -Plan
```

- If the TV shows **"Allow USB debugging?"**, tell the human right away: tick *Always allow*, press *Allow*.
- If port 5555 is refused, the TV needs a pairing code: ask the human to open *Developer options > Wireless
  debugging > Pair device with pairing code*, then `adb pair <ip>:<pairport> <code>`, `adb connect <ip>:<port>`
  (the port on the Wireless debugging screen), `adb -s <ip>:<port> tcpip 5555`, and re-run the plan.

Read `my-tv/<tv>/plan.json` and show the human a short table. Use these risk words:

| plan.json | Say to the human | Meaning |
|---|---|---|
| `debloat.low` | **Low risk, recommended** | junk: tracking, ads, demo mode, auto-installers. Safe on every TV it's listed for. |
| `debloat.questions[].affects` | **Medium risk, your call** | only matters if you use that feature |
| `debloat.appCandidates` | **Your call** | apps that came with the TV; off only if you never use them |
| `debloat.keepPresent` | **High risk, never touched** | inputs, remote, updates, casting, setup, accessibility |
| `debloat.notInCatalog` | **Unknown, left alone** | not enough evidence either way |
| `background.asleep / stopOnly / protect` | sleep plan for apps | see step 4 |

Entries marked `verified: tested` were checked end to end on a real TV by this project; `community` ones come
from community-tested lists for that brand. Mention which ones are which when the human is unsure.

## 4. Answers file, preview, run

Copy `answers-template.json` and fill it from the human's replies:

```json
{
  "steps": { "debloat": true, "background": true, "stick": true, "remote": false, "launcher": false, "extras": false },
  "features": { "content": "off", "antenna": "keep", "airplay": "keep", "cast": "keep", "screensaver": "off" },
  "disableApps": ["com.example.preinstalled.app"],
  "skipPackages": [],
  "background": { "asleep": [], "stopOnly": ["com.spotify.tv.android"], "protect": [] },
  "animation": "0.5",
  "dailyRefresh": true,
  "privateDns": false,
  "smartTubeLinks": true,
  "testRestart": true
}
```

- `features`: `"off"` switches off that question's packages, `"keep"` leaves them.
- `dailyRefresh`: refresh once a day from 4:00 (`dailyRefreshHour` to change), never while something plays.
  `privateDns` (AdGuard, only with the human's explicit yes) and `smartTubeLinks` belong to the `extras` step.
- `background`: only list overrides; everything else uses the plan's suggestion. *asleep* = no background
  running until opened (default for installed apps), *stopOnly* = stopped at startup but may run after the human
  starts it (music, VPN), *protect* = never touched.

Preview, show the human the DRYRUN lines that matter, get a yes, then run for real:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests\DryRun.ps1 -Device <ip> -Answers my-tv\<tv>\answers.json -Auto
powershell -NoProfile -ExecutionPolicy Bypass -File setup\Wizard.ps1 -Device <ip> -Answers my-tv\<tv>\answers.json -Auto
```

A full run takes 10-20 minutes and restarts the TV once at the end, so run it in the background, write its output
to a log file and follow that. Relay every line starting with `ON THE TV >>` to the human immediately; the wizard
is waiting for them. Lines with `[x]` are failures, `[!]` are warnings, `[ok]` is progress. Re-running is safe:
steps that are already done are detected and skipped.

## 5. Steps that need the human's hands

| Step | What the human does | What you do |
|---|---|---|
| Boot helper (stick) | Approve "Allow USB debugging?" for Termux on the TV | Nothing; the wizard types into Termux and waits |
| ADB Auto Enable (Android 14+) | Wireless debugging > *Pair device with pairing code*, read you the 6-digit code | `Wizard.ps1 -Device <ip> -PairCode <code>` (add `-PairPort` if it asks). If the wizard says it couldn't reach the setup page, that's normal on TVs that already use its port (Xiaomi): if the restart test passes, no pairing is needed. |
| Remote button (Key Mapper) | Open Key Mapper's *Expert Mode* screen once (choose *Don't allow* if it asks to send notifications); create the key map (steps are printed); press Home, then the button | Watch for "It works!" |
| Launcher (Projectivy) | Press *Install* in the Play Store; accept Projectivy's own setup; in the wallpaper plugin set type *Wallhaven* and the Booru URL the wizard prints (top wallpapers of the last 6 months in the TV's resolution, see [docs/06](docs/06-launcher-and-wallpapers.md#rotating-wallpapers-from-wallhaven)); re-select the plugin as Projectivy's wallpaper source so it fetches the new list | Nothing |

## 6. Finish

The wizard restarts the TV (if `testRestart`), checks that the boot helper re-applied everything by itself,
and writes `my-tv/<tv>/report.md`. Summarise it for the human in 3-4 lines: what was switched off, free memory
before/after, whether it survives restarts, and how to undo (`Start-Here.cmd` > Undo everything).

To double-check afterwards (read-only): `adb shell -n tail /data/local/tmp/tv-apply.log` shows one
`apply start (uptime ...)` line about 80 s and another about 210 s after every restart, and
`adb shell -n cmd notification list | grep keymapper` should print nothing.

## 7. TVs the catalog doesn't know well

`notInCatalog` packages are left alone on purpose. If the human wants to go further:

1. List candidates: `adb shell -n pm list packages -s` minus everything in `plan.json`. Look for names with
   *analytics, statistic, tracking, acr, telemetry, log, feedback, demo, shop, hotel, ad, promo, recommend*.
2. For each one, check what it does before suggesting it:
   `adb shell -n dumpsys package <pkg> | grep -E 'persistent|SYSTEM|android.media.tv|BOOT_COMPLETED|HOME|InputMethod|Accessibility'`.
   Anything that is persistent, provides a TV input, handles HOME, input methods, accessibility, HDMI/CEC,
   remote pairing, updates or setup is **high risk: don't**.
3. Present each as Low / Medium / High risk with your reason, and only switch off what the human approves,
   **one at a time**, testing Home, an HDMI input, a streaming app and the remote after each:
   `adb shell -n pm disable-user --user 0 <pkg>`. Undo with `adb shell -n pm enable --user 0 <pkg>`.
4. If it works, suggest the human contributes the line to `catalog/packages.csv` (with brand and model).

## 8. If something goes wrong

- The wizard rolls a batch back by itself if Android restarts, Home breaks or a setup/recovery screen appears.
  Rolled-back packages are remembered and skipped next time.
- Home button dead: `adb shell -n pm enable --user 0 com.google.android.apps.tv.launcherx` (Google TV) and
  `... com.google.android.tungsten.setupwraith`.
- Everything: `Wizard.ps1 -Device <ip> -Undo -Auto`, then restart the TV.
- More: [docs/08-troubleshooting.md](docs/08-troubleshooting.md).
