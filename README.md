# Leaf it Alone Mod Launcher & Loader

A GUI launcher and a native mod loader for *[Leaf it Alone](https://store.steampowered.com/app/3981100/)*
(Unity 6000.5.6f1, IL2CPP) on Windows. Install the loader and safe-base in one click,
browse mods from GitHub and install them, tweak their settings, and play — all without
touching a script or editing JSON by hand.

## Quick start

1. **Download** the latest `LeafItAloneLauncher-*-win-x64.zip` from
   [Releases](https://github.com/RemagOfficial/LeafItAloneModLauncher/releases)
   and unzip it anywhere. It's self-contained — no .NET install needed.
2. **Run** `LeafItAloneLauncher.exe`. On the Dashboard, install the **mod loader + safe-base**
   (one action).
3. **Browse → Install** a mod, toggle it on, hit **Apply & Launch**.

That's it. Mods are pulled from GitHub through the launcher — you don't need this repo
checked out to use them.

## What you get

- **A launcher, not a rewrite** — one WPF app that drives the tested safety engines and
  patchers as subprocesses, so every guarantee holds unchanged.
- **A real mod loader (`LiaLoader`)** that hosts **code mods** alongside classic **data mods**
  (UnityPy bundle edits). Data mods need a one-time Python 3.12 + UnityPy step (the launcher
  guides you); code mods need nothing extra.
- **Safe, offline play.** Modded runs never touch the leaderboards or your real save — the
  firewall block, Steam-proxy and save isolation are installed once and verified.
- **Mods from GitHub.** Add any repo that follows the [mod source spec](launcher/docs/MOD_SOURCE_SPEC.md);
  the launcher lists, installs, updates and self-heals those mods like the built-ins.
- **Config from `mod.json`** — each mod's options render as real controls, no hand-editing.

Shipped mods: `bag-capacity`, `faster-bag-tying`, `more-leaves`, `sakura-petals-everywhere`
(data) and `blower-suction-all-maps` (code).

## Layout

| Folder | What it is | More |
|---|---|---|
| `launcher/` | The GUI app — **start here** | [README](launcher/README.md) |
| `loader` (repo-root `loader/`) | The mod loader DLLs the launcher installs | [launcher README](launcher/README.md) |
| `safe-base/` | The offline-safety framework the launcher installs | [README](safe-base/README.md) |
| `content-mods/` | The data + code mods | [README](content-mods/README.md) |
| `overlay/` | Standalone native-overlay example (cosmetic) | [README](overlay/README.md) |

Changes across releases are in [CHANGELOG.md](CHANGELOG.md).

## Mod it yourself

Fork the ready-made [`launcher/docs/sample-source-repo/`](launcher/docs/sample-source-repo/)
and follow [`PUBLISH_YOUR_MODS.md`](launcher/docs/PUBLISH_YOUR_MODS.md) to ship your own
mod repo the launcher can install from.

## Requirements & safety

- Windows x64, and **you must own** *Leaf it Alone* on Steam — no game files are shipped here.
- The game has **no anti-cheat** but *does* have online leaderboards, which is exactly why the
  safe-base exists: network-blocking alone isn't enough, so modded play runs on a throwaway save
  with Steam kept in the dark. Full rationale in [`safe-base/README.md`](safe-base/README.md).

## Credits & disclosure

Built with **AI assistance** (Claude coding agent); every in-game result verified by hand.
Tooling: [UnityPy](https://github.com/Logardas/UnityPy). The launcher is in-box .NET 8 / WPF
with no third-party NuGet packages; the `version.dll` / `steam_api64.dll` proxies are first-party
Win32 builds, not game code.

## License

MIT for everything in this repo. You must own the game to run any of it.
**No game files or extracted game assets are redistributed.**
