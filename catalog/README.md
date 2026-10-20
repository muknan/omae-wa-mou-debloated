# Catalog

What the wizard (and an AI assistant using [AI-SETUP.md](../AI-SETUP.md)) knows about TV packages.
Only packages that are actually installed on your TV are ever considered; everything else in here is ignored.

| File | What's in it |
|---|---|
| [packages.csv](packages.csv) | System packages by brand, with a risk level |
| [questions.json](questions.json) | The "do you use X?" questions behind the medium-risk packages |
| [background.csv](background.csv) | Known streaming apps (kept asleep), music apps (stopped at startup only) and helpers (never touched) |
| [helpers.json](helpers.json) | Helper apps the wizard can install, pinned to tested versions with their official SHA-256 |

## packages.csv

| Column | Values |
|---|---|
| `package` | package name; `*` wildcards allowed |
| `risk` | `low`: junk, switched off for everyone. `medium`: depends on a feature (see `feature`). `keep`: never touched, even if it looks like junk |
| `feature` | for `medium`: `content`, `antenna`, `airplay`, `cast`, `mirroring`, `alexa`, `screensaver`, `usb`, `smarthome` |
| `brand` | where it was seen |
| `verified` | `tested`: checked end to end on a real TV by this project. `community`: reported working in community-tested lists |
| `what` | plain-English description |

Rules the catalog follows:

- If **any** tested list reports a package as needed on some model ("load-bearing", "keep", "do not disable"),
  it is `keep` here, even if other models switch it off fine. Better a little less debloated than a broken TV.
- Firmware updaters, inputs/HDMI, remote pairing, setup, keyboards, accessibility, casting (DIAL) and the stock
  launchers are always `keep`.
- Packages that aren't in the catalog are left alone.

## Sources

- Our own tests on a Xiaomi TV X Pro 2025 (Google TV 14), see [docs/02](../docs/02-audit-and-debloat.md).
- The per-model lists in [Gathin23/cleanyourtv](https://github.com/Gathin23/cleanyourtv) (MIT), tested on
  90+ TVs and boxes. Descriptions were rewritten and conflicts resolved toward `keep`.

## Adding your TV

Found something safe to switch off (or something that must stay)? Add a line with your brand, test it on your
TV (Home, an HDMI input, a streaming app, the remote, after a restart), and open a pull request.
