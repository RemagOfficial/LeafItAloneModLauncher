# Leaf it Alone Mod Launcher & Loader

A GUI launcher and a native mod loader for *[Leaf it Alone](https://store.steampowered.com/app/3981100/)*
(Unity 6000.5.6f1, IL2CPP) on Windows. Install the loader and safe-base in one click, browse
mods from GitHub, install them, tweak their settings and play. There is no script to run and
no JSON to edit.

## Quick start

1. **Download** the latest `LeafItAloneLauncher-*-win-x64.zip` from
   [Releases](https://github.com/RemagOfficial/LeafItAloneModLauncher/releases) and unzip it
   anywhere. It is self-contained, so there is no .NET install step.
2. **Run** `LeafItAloneLauncher.exe`. On the Dashboard, install the mod loader and safe-base
   in one action.
3. **Browse → Install** a mod, toggle it on, and click **Apply & Launch**.

Mods are fetched from GitHub through the launcher. You don't need this repo checked out to
use them.

## What you get

- The launcher is a WPF front end for the tested safety engines and patchers, which it runs
  as subprocesses. Every guarantee those scripts make still holds.
- LeafLoader hosts code mods. Alongside the classic data mods (UnityPy bundle edits), it runs
  code mods written against its API. Data mods need a one-time Python 3.12 + UnityPy step,
  which the launcher walks you through. Code mods need nothing extra.
- Modded play stays offline and off your real save. The firewall block, Steam proxy and save
  isolation are installed once and verified on launch.
- Any GitHub repo that follows the [mod source spec](launcher/docs/MOD_SOURCE_SPEC.md) works
  as a source. The launcher lists, installs, updates and repairs those mods the same way as
  the built-ins.
- Each mod's options are rendered as controls from its `mod.json`, so there is nothing to
  hand-edit.

Shipped mods: `bag-capacity`, `faster-bag-tying`, `more-leaves`, `sakura-petals-everywhere`
(data) and `blower-suction-all-maps` (code).

## Layout

| Folder | What it is | More |
|---|---|---|
| `launcher/` | The GUI app. Start here. | [README](launcher/README.md) |
| `loader/` | The LeafLoader files the launcher installs into your game. | [launcher README](launcher/README.md) |
| `safe-base/` | The offline-safety framework the launcher installs. | [README](safe-base/README.md) |
| `content-mods/` | The data and code mods. | [README](content-mods/README.md) |
| `overlay/` | A standalone native-overlay example, cosmetic only. | [README](overlay/README.md) |

Release notes are in [CHANGELOG.md](CHANGELOG.md).

## Make your own

Fork the ready-made [`launcher/docs/sample-source-repo/`](launcher/docs/sample-source-repo/)
and follow [`PUBLISH_YOUR_MODS.md`](launcher/docs/PUBLISH_YOUR_MODS.md) to publish a mod repo
the launcher can install from.

## Requirements & safety

- Windows x64. You must own *Leaf it Alone* on Steam. No game files are shipped here.
- The game has no anti-cheat. It does post to online leaderboards, which is why the safe-base
  exists. A network block on its own leaves gaps, so modded play uses a throwaway save and
  never reports to Steam. The full reasoning is in
  [`safe-base/README.md`](safe-base/README.md).

## Credits & disclosure

Built with AI assistance (Claude coding agent), with every in-game result checked by hand.
Tooling: [UnityPy](https://github.com/Logardas/UnityPy). The launcher is .NET 8 / WPF with no
third-party NuGet packages. The `version.dll` and `steam_api64.dll` proxies are first-party
Win32 builds written for this repo.

## License

MIT for everything in this repo. You must own the game to run any of it.
**No game files or extracted game assets are redistributed.**
