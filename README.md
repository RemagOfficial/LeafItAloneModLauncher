# Leaf it Alone — Modding

Everything needed to mod *[Leaf it Alone](https://store.steampowered.com/app/3981100/)*
(Unity 6000.5.6f1, IL2CPP) on Windows: a native overlay example, an offline-safety
framework that content mods depend on, and the content mods themselves.

## Layout

```
LeafItAloneModding/
├── overlay/         Standalone native-overlay example (a working hello-world)
├── safe-base/       Offline-safety framework — required for any content mod
└── content-mods/    One folder per content mod, each depends on safe-base/
```

### `overlay/`
A minimal, self-contained **native overlay mod** that draws a HUD (title, clock,
RAM/CPU) over the game via a `version.dll` proxy. **Cosmetic only** — it changes
nothing in gameplay. Kept as a working example of the DLL-hijack technique on a
protected Unity IL2CPP build. Fully shipped, documented and videoed.

### `safe-base/`
The **offline-safety framework**. Installs once, keeps modded play from ever
touching the game's leaderboards. Provides:
- Windows Firewall outbound-block on `Leaf it Alone.exe`, verified at install.
- A `steam_api64.dll` proxy that makes `SteamAPI_Init` fail — the game thinks
  Steam isn't running, so **Steamworks achievements and leaderboards can't
  record anything**.
- **Save isolation** on install (real save moved aside, empty `UserData/` made);
  the modded save is **discarded on uninstall** so queued submissions never
  leak when you go back online.
- A `version.dll` overlay that **refuses to run** unless all three preconditions
  hold, and shows a persistent `OFFLINE · MODDED PLAY · SCORES WILL NOT BE
  SUBMITTED` banner so you always know.
- A PowerShell SDK (`base.psm1`) with `Assert-SafeBaseActive`,
  `Backup-Original`, `Register-Mod` / `Unregister-Mod` that content mods
  import — so no content mod ever has to repeat any of this.

Install `safe-base` once. Content mods depend on it. **If you plan to change
in-game numbers, install the base first.**

### `content-mods/`
Small patchers that change what's actually *in* the game — data-driven edits to
Unity `AssetBundle`s in `StreamingAssets/aa/StandaloneWindows64/`. The
`global-metadata.dat` encryption only blocks the managed-code path (BepInEx /
MelonLoader); content in the `.bundle` files is plain UnityFS with intact
TypeTrees, so we can read and rewrite MonoBehaviour / ScriptableObject fields
directly with [UnityPy](https://github.com/Logardas/UnityPy).

First planned mod: **bag-capacity** — increase leaf-bag size (the four
`Small/Medium/Big/Giant BagCapacity` ScriptableObjects in
`coreassets_assets_meta_*.bundle`, currently 50 / 200 / 500 / 800).

Every content mod follows the same pattern:
```
content-mods/<name>/
├── apply.ps1     imports base.psm1, asserts base active, backs up, applies
├── revert.ps1    restores from backup, unregisters with the base
├── <patch>.py    the actual UnityPy / file edit
└── README.md     what it changes; requires `safe-base ≥ 1.0`
```

## Quick start

| I want to… | Do this |
|---|---|
| Just see how the DLL-hijack overlay works | Follow [`overlay/README.md`](overlay/README.md) |
| Play the game with mods without polluting the leaderboard | Install [`safe-base/`](safe-base/README.md) once |
| Actually change gameplay content (bigger bag, more leaves, faster seal) | Install `safe-base/`, then `apply.ps1` from a mod in [`content-mods/`](content-mods/README.md) |
| Undo everything and go back to vanilla | `safe-base/uninstall-all.ps1` — reverts every registered content mod, restores your original save, removes the firewall rule |

## Why the split exists

Blocking the network alone is not enough. Users uninstall mods, forget the
block, play vanilla on the same save file that was used while modded, and any
queued submissions (or Steam-synced achievements) leak to the leaderboard. The
only way to make this safe for an average user is **structural**: modded play
must be on a *different, throwaway save*, and the Steam client must never be
told anything at all while modded. That's what `safe-base/` guarantees, so no
content mod ever has to think about it.

## Game protection — the state of play

The build we're targeting is protected:
- `global-metadata.dat` is encrypted (decrypt-on-demand, never fully plaintext
  in memory).
- All 263 `GameAssembly.dll` exports are renamed and wrapped in virtualised
  dispatchers.
- BepInEx 6 and MelonLoader 0.7.3/0.7.4-ci bootstrap but never finish
  initialising the managed runtime, so ordinary C# mods can't load.

Which is why every path in this repo sidesteps the managed layer:
`overlay/` uses a native DLL proxy, and `content-mods/` edit AssetBundles. The
metadata-blocked path (real C# mods, Harmony patches, managed injection) is
documented in each README as future work if the protection is ever broken.

## Requirements
- Windows x64.
- **You must own** *Leaf it Alone* on Steam. No game files are shipped here.
- Single-player / offline. The game has **no anti-cheat** but *does* have
  online leaderboards — which is the reason `safe-base/` exists.

## Credits & disclosure
- Built with AI assistance (Claude).
- Tooling: [UnityPy](https://github.com/Logardas/UnityPy) for bundle edits,
  [`um` (universal-modder)](https://github.com/rehan-remade/universal-modder)
  for the showcase video.
- Uses only Win32 / GDI / PowerShell APIs. No third-party game code.

## License
MIT for everything in this repo. You must own the game to run any of it.
**No game files or extracted game assets are redistributed.**
