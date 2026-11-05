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
touching the game's leaderboards or your real save file. **Status: M1 + M2 +
M3 + M4 landed and in-game tested, M5 (the `base.psm1` SDK + content-mod
registry + `uninstall-all.ps1`) landed and smoke-tested (2026-11-05).** The firewall block, the
Steamworks stub proxy AND save isolation are
implemented, reversible and verified. The game launches normally, the
leaderboard renders empty and a modded play-session writes to a throwaway
`Saves/` that never touches your real one. M4 layers a persistent
in-game banner + a one-shot startup probe on top of those three, merging
into the same `version.dll` the standalone overlay ships. Four upload paths
closed, one leak class mitigated, one visible-verifier added:
- **[M1 ✅]** Windows Firewall outbound-block on `Leaf it Alone.exe`,
  verified at install (rule + service + manifest). Closes the game's own
  HTTPS path.
- **[M2 ✅]** A `steam_api64.dll` stub proxy (1089 exports, all returning
  0) that makes `SteamAPI_Init` fail. Closes every Steamworks-mediated path
  — leaderboard upload, achievements, stats, cloud saves — because the game
  process never gets a valid interface to call. Game launches cleanly with
  the proxy in place; the in-game leaderboard renders empty.
- **[M3 ✅]** **Save isolation**. `install.ps1` moves the game's
  `AppData\LocalLow\Eternity\Leaf it Alone\Saves\` folder to
  `%LOCALAPPDATA%\LeafItAloneSafe\backup\saves-<ts>\` and creates a fresh
  empty placeholder. Modded progression writes there. `uninstall.ps1`
  restores the original Saves/ (the modded one is **parked**, never
  deleted). Refuses to run if the game is currently active. If Steam Cloud
  is enabled on the game, install prints a warning with the click-path to
  disable it manually (safe-base never edits Steam's own config).
- **[M4 ✅]** A persistent `OFFLINE · MODDED PLAY · SCORES WILL NOT BE
  SUBMITTED` banner drawn from inside the game's own process (the same
  `version.dll` that ships the cosmetic HUD), with a **one-shot** startup
  check: file-size match on the Steam proxy, presence of the `Saves/`
  isolation marker, and a live `connect(1.1.1.1:443)` outbound probe that
  only the game process can perform meaningfully. Failing any check turns
  the banner red; safe-base does not police manual tampering mid-play.
  In safe-base mode the overlay is **minimal banner-only**: the HUD panel
  is not drawn at all and the banner is plain outlined text (no box) so it
  stays readable without distracting during play. `install.ps1` backs up
  any pre-existing `version.dll` (e.g. a standalone overlay install) before
  dropping ours; `uninstall.ps1` restores the overlay slot FIRST so your
  prior setup comes back before anything else changes.
- **[M5 ✅]** A **self-contained** PowerShell SDK (`base.psm1`) that
  `install.ps1` deploys to `%LOCALAPPDATA%\LeafItAloneSafe\base.psm1`, plus a
  separate `mods.json` content-mod registry and `uninstall-all.ps1`. Content
  mods import that one file and get `Assert-SafeBaseActive`,
  `Assert-SafePreconditions` (re-checks all four layers right now),
  `Backup-Original`, `Register-Mod` / `Unregister-Mod`, `Restore-ModFiles` and
  `Revert-Mod` — so no content mod ever repeats any of the safety code, and
  every file it touches is backed up and reversible. `uninstall.ps1` refuses
  while any mod is registered; `uninstall-all.ps1` reverts them all then tears
  down the base.

Install `safe-base` once. Content mods depend on it. **If you plan to change
in-game numbers, install the base first** — M5 is landed, so the SDK content
mods build on is in place; the first real content mod (`bag-capacity`, M6) is
the SDK's end-to-end test. See the roadmap in
[`safe-base/README.md`](safe-base/README.md).

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
| Try the M1 + M2 + M3 + M4 + M5 safety framework now (no content mods yet) | Close the game. `safe-base/install.ps1` → play (green banner appears at top-centre) → `safe-base/uninstall.ps1`. Status any time: `safe-base/status.ps1` |
| Back out of everything (mods + base) in one go | `safe-base/uninstall-all.ps1` — reverts every registered content mod, then uninstalls the base |
| Play the game with mods without polluting the leaderboard (M1-M5 all landed) | Install `safe-base/` once |
| Actually change gameplay content (bigger bag, more leaves, faster seal) | The SDK is ready (M5 landed); the first mod (`bag-capacity`, M6) is pending. Then: install `safe-base/`, then `apply.ps1` from a mod in [`content-mods/`](content-mods/README.md) |
| Undo everything and go back to vanilla | `safe-base/uninstall-all.ps1` — reverts every registered content mod, restores your original save, removes the firewall rule LAST. With no mods applied it's equivalent to `safe-base/uninstall.ps1`. |

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
