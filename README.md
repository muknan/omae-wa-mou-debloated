# omae-wa-mou-debloated

**Other debloats clean your TV once. This one keeps it clean.**

Make a slow Google TV fast again, and keep it that way after every restart. No rooting, nothing permanent, and you can undo all of it.

*Omae wa mou debloated.* Your apps are already asleep. Even after a reboot.

<p align="center">
  <img src="screenshots/home_wp6.jpg" width="49%" />
  <img src="screenshots/home_wp1.jpg" width="49%" />
  <img src="screenshots/home_wp2.jpg" width="49%" />
  <img src="screenshots/home_wp5.jpg" width="49%" />
</p>

## What this does

- Switches off the junk apps your TV came with (ads, tracking, stuff you never use)
- Freezes every app you're not watching, including ones you install later
- Makes menus feel snappier
- Puts itself back after every restart, with no PC needed (optional)
- Gives you a "fresh boot" button on your remote (optional)
- Everything can be undone with one command

Made on a Xiaomi Google TV, but it works on most Google TVs.

## Why this one?

Most TV debloat guides switch off some apps, add a new home screen, and stop there.
Then the TV restarts and quietly undoes half of it: background limits reset, menus slow down again, and wireless debugging switches itself off.

| | Typical debloat | This kit |
|---|---|---|
| Switches off junk apps | ✅ | ✅ |
| Keeps apps asleep in the background | ❌ | ✅ every app, even new installs |
| Still working after a restart | half of it | ✅ the TV re-applies everything itself |
| Wireless debugging turns itself back on | ❌ | ✅ with ADB Auto Enable |
| One remote button to make the TV feel freshly restarted | ❌ | ✅ with Key Mapper |
| Checked with real before/after numbers | rarely | ✅ [see results](docs/07-measuring.md) |

## What you need

- A **Windows PC** on the **same Wi-Fi** as your TV
- About **20 minutes**
- Your TV remote

---

## Step 1: Download this kit

1. At the top of this page, click the green **Code** button, then **Download ZIP**.
2. Find the ZIP in your Downloads folder, right-click it, and choose **Extract All**.
3. Open the extracted folder (`omae-wa-mou-debloated-main`).
4. Right-click an empty spot inside the folder and choose **Open in Terminal**.

A blue or black window opens. That's where you'll paste the commands below.
Paste this one first so Windows lets the kit run:

```powershell
Get-ChildItem -Recurse | Unblock-File; Set-ExecutionPolicy -Scope Process Bypass -Force
```

## Step 2: Install the tool that talks to your TV

Paste this and press Enter:

```powershell
winget install --id Google.PlatformTools -e
```

When it finishes, **close the window and open it again** (Step 1, point 4). Then paste Step 1's command again.

## Step 3: Unlock developer options on the TV

On your TV, with the remote:

1. Go to **Settings > System > About**.
2. Scroll to **Android TV OS build** and press OK on it **7 times**. You'll see "You are now a developer".
3. Go back to **Settings > System > Developer options**.
4. Turn on **Wireless debugging** (on some TVs it's called **Network debugging**).
5. Write down the **IP address** shown there. It looks like `192.168.1.50`.

## Step 4: Connect your PC to the TV

In the commands below, **replace `192.168.1.50` with your TV's IP address** from Step 3.

```powershell
adb connect 192.168.1.50:5555
```

A box pops up on the TV asking to allow debugging. Tick **Always allow from this computer** and press **Allow**.

You should see `connected to 192.168.1.50:5555`.
Didn't work? See [Stuck?](#stuck) below.

## Step 5: Switch off the junk apps

First, see what would be switched off. This changes nothing:

```powershell
.\scripts\pc\Debloat.ps1 -Device 192.168.1.50:5555 -List .\lists\xiaomi-google-tv.txt -Tier LOW -WhatIf
```

Happy with the list? Run it for real:

```powershell
.\scripts\pc\Debloat.ps1 -Device 192.168.1.50:5555 -List .\lists\xiaomi-google-tv.txt -Tier LOW
```

It stops after every 3 apps. Each time, quickly check that the **Home button**, an **HDMI input** and **a streaming app** still work, then press Enter in the window to continue.

> **Not on a Xiaomi TV?** The list is for Xiaomi. It only switches off apps your TV actually has, but check the preview carefully.

## Step 6: Keep apps asleep in the background

```powershell
.\scripts\pc\Deploy-Scripts.ps1 -Device 192.168.1.50:5555
```

This puts apps you're not using to sleep and speeds up the menu animations. Your launcher, keyboard and anything playing on screen are never touched.

**That's it. Your TV is faster.**

---

## Optional extras

| Want this? | Guide |
|---|---|
| Keep everything working after the TV restarts, with **Termux** and **ADB Auto Enable** (otherwise, run Step 6 again after a restart) | [Make it stick](docs/04-boot-agent.md) |
| Remote button tricks with **Key Mapper** (free): add a "refresh my TV" button, or turn spare buttons (like Live TV) into app shortcuts | [Remote buttons](docs/05-key-mapper.md) |
| A clean home screen with rotating wallpapers like the screenshots | [Launcher & wallpapers](docs/06-launcher-and-wallpapers.md) |
| More switches: antenna TV, AirPlay, PatchWall (only if you don't use them) | [Junk apps in detail](docs/02-audit-and-debloat.md) |

## Undo everything

```powershell
.\scripts\pc\Debloat.ps1 -Device 192.168.1.50:5555 -Restore
.\scripts\pc\Deploy-Scripts.ps1 -Device 192.168.1.50:5555 -Remove
```

Then restart the TV. It's back to how it was.
If you did the optional "Make it stick" guide, also uninstall **Termux** and **Termux:Boot** from the TV's app settings.

## Stuck?

| Problem | Fix |
|---|---|
| "running scripts is disabled" | Paste the command from Step 1 again. |
| `adb` is not recognized | Close the window and open it again after Step 2. |
| "cannot connect" or "refused" | Make sure **Wireless debugging** is still on (it switches off if the TV loses Wi-Fi), and that the PC and TV are on the same Wi-Fi. |
| No popup on the TV | Look at the TV screen while you run Step 4. It can hide behind an app. |
| Still can't connect on `:5555` | Your TV may need a pairing code instead. Follow [Connecting with a pairing code](docs/01-adb-setup.md#if-port-5555-is-refused-pair-with-a-code). |
| Something stopped working | Run the **Undo** commands above. |

More fixes: [Troubleshooting](docs/08-troubleshooting.md)

---

<details>
<summary><b>For the curious: results, how it works, tools</b></summary>

### Results on the test TV (Xiaomi TV X Pro 2025, 2 GB RAM)

| | Before | After |
|---|---|---|
| Free memory after a restart | 530 MB | 612 MB |
| Apps running after a restart | 40 | 33 |
| Apps that start by themselves | running | asleep |

### How it works

- Junk apps are **disabled, not deleted**, so they come back with one command.
- Android forgets "keep this app asleep" every time the TV restarts. The optional [boot helper](docs/04-boot-agent.md) quietly sets it again after each restart.
- Every tweak was tested on a real TV. Some popular tips made things worse and were left out: see [what we measured](docs/07-measuring.md).

### Tools used

| Tool | What for |
|---|---|
| [Android Platform-Tools](https://developer.android.com/tools/releases/platform-tools) | lets the PC talk to the TV |
| [Projectivy Launcher](https://play.google.com/store/apps/details?id=com.spocky.projengmenu) | ad-free home screen |
| Booru Wallpaper Provider (Projectivy plugin) | rotating wallpapers from [Wallhaven](https://wallhaven.cc) |
| [Termux](https://github.com/termux/termux-app) + [Termux:Boot](https://github.com/termux/termux-boot) | re-applies settings after a restart |
| [ADB Auto Enable](https://github.com/mouldybread/adb-auto-enable) | turns wireless debugging back on after a restart |
| [Key Mapper](https://github.com/keymapperorg/KeyMapper) | remote button tricks |

### All guides

[Connecting](docs/01-adb-setup.md) · [Junk apps](docs/02-audit-and-debloat.md) · [Settings tweaks](docs/03-settings-tweaks.md) · [Make it stick](docs/04-boot-agent.md) · [Remote buttons](docs/05-key-mapper.md) · [Launcher & wallpapers](docs/06-launcher-and-wallpapers.md) · [Measuring](docs/07-measuring.md) · [Troubleshooting](docs/08-troubleshooting.md)

</details>

## Good to know

- No rooting, nothing deleted, nothing that can brick your TV.
- Keeping wireless debugging on means devices on your Wi-Fi *could* ask to connect, but only ones you've approved get in. Use it on your home Wi-Fi only.
- Not affiliated with Xiaomi, Google or any app above. MIT license, use at your own risk.
