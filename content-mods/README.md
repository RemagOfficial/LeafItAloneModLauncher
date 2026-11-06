# `content-mods/` — Gameplay patches

Small, focused mods that change what's *in the game* — bag sizes, leaf counts,
seal times, textures, new achievements, whatever the bundle data can express.
Each mod lives in its own folder and depends on [`../safe-base/`](../safe-base/README.md).

**Status: two have shipped.** [`bag-capacity/`](bag-capacity/README.md)
(M6) doubles the four leaf-bag capacities and is the SDK's real end-to-end
test. [`more-leaves/`](more-leaves/README.md) (L7) densifies every leaf area and
is the launcher's proof that a second UnityPy mod is added purely from its
`mod.json` (no app code). It is built and verified offline, awaiting the in-game
check. Note its v1.0 targeted the wrong bundle: patching the
`coreassets_assets_areas*` `leavesCount` had **no visible effect**, so in-game RE
moved it (v1.1) to the real spawner — the `initArgs[].leavesCount` on the
`Eternity.Gameplay.Leaves.Area` layout MonoBehaviour baked into the three
gameplay **scene** bundles (castle / hometown / sakura). See that README for the
data model. The rest of this folder's table is still planned.

## Why we can content-mod at all

`global-metadata.dat` is encrypted (BepInEx and MelonLoader can't finish
initialising), so C# mods are blocked. But the actual gameplay content lives
in `StreamingAssets/aa/StandaloneWindows64/*.bundle` as **plain UnityFS**
AssetBundles — 123 of them, **not encrypted**, with intact TypeTrees. We can
open them with [UnityPy](https://github.com/Logardas/UnityPy) and rewrite
MonoBehaviour / ScriptableObject fields directly:

```
StreamingAssets/aa/StandaloneWindows64/
├── coreassets_assets_actors       ← leaf-bag prefabs (Small/Medium/Big/Giant)
├── coreassets_assets_meta         ← *capacity numbers* for those bags
├── coreassets_assets_leaves       ← leaf data
├── coreassets_assets_collectables ← x3 shards
├── coreassets_assets_modifiers    ← x3 shards
├── coreassets_assets_objectives   ← x3 shards (incl. `SellWorthyBag`)
├── coreassets_assets_gamefeatures ← x3 shards
├── coreassets_assets_areas        ← x3 shards, each has `leavesCount`
├── coreassets_assets_achievements ← x2 shards
├── coreassets_assets_contexts
├── coreassets_assets_entries
├── visuals_assets_bags            ← bag models / textures
├── visuals_assets_tools           ← rake / tool visuals
└── ... (123 total)
```

## The rule

> **A content mod never touches the network, the firewall, the save, or
> Steam.** All of that is `safe-base`'s job. A content mod is *just the
> payload*.

`apply.ps1`'s first real line is always `Assert-SafeBaseActive`. If the base
isn't installed, the mod refuses to run with a clear link to `../safe-base/`.

## Layout (skeleton for each mod)

```
content-mods/<modname>/
├── README.md         what it changes, screenshot, "requires safe-base ≥ 1.0"
├── apply.ps1         the installer, imports base.psm1, calls Backup-Original,
│                     runs the patch, calls Register-Mod
├── revert.ps1        restore from the base's backup, Unregister-Mod
├── patch.py          the actual UnityPy / file edit (or C++, whatever fits)
└── LICENSE.md        MIT
```

`apply.ps1` looks like (this is exactly what
[`bag-capacity/apply.ps1`](bag-capacity/apply.ps1) does):

```powershell
$sdk = Join-Path $env:LOCALAPPDATA "LeafItAloneSafe\base.psm1"
if (-not (Test-Path $sdk)) { Write-Error "Install safe-base first: ../safe-base/"; exit 2 }
Import-Module $sdk -DisableNameChecking     # the SDK's verbs are non-approved; ignore it

if (Test-GameRunning) { Write-Error "Close the game first"; exit 3 }
Assert-SafeBaseActive                        # base must be installed
Assert-SafePreconditions                     # firewall + proxy + save + banner healthy NOW

$target = "<resolved path to the game's bundle>"
# 1. stage + self-verify the patched copy BEFORE touching the game folder
& C:\Python312\python.exe patch.py --in $target --out-dir $staging --scale 2
# 2. snapshot the pristine original through the SDK, then overwrite + re-verify
$rec = Backup-Original -ModName "bag-capacity" -TargetPath $target
Copy-Item $stagedBundle $target -Force
Register-Mod -Name "bag-capacity" -Version "1.0.0" -Files @($rec)
```

`revert.ps1` is symmetric — the SDK does the restore (park-aside, never delete)
and unregistration in one call:

```powershell
Import-Module (Join-Path $env:LOCALAPPDATA "LeafItAloneSafe\base.psm1") -DisableNameChecking
Revert-Mod -Name "bag-capacity"     # restores byte-identical original + unregisters
```

## Rules a content mod must follow

- **Only edit files under `StreamingAssets/aa/StandaloneWindows64/`**, or add
  new files under an obviously-modded prefix. Never touch `global-*`,
  `level*`, `resources.assets` (encrypted), `GameAssembly.dll`,
  `UnityPlayer.dll`, or anything else in the game folder that isn't a plain
  bundle.
- **Never** install DLLs or firewall rules — that's `safe-base`.
- **Always** call `Backup-Original` before the edit, and register files with
  the base so `uninstall-all.ps1` can cleanly undo everything.
- **Null the bundle's catalog CRC.** This game uses Unity Addressables 2.9,
  which verifies every bundle against a CRC in `aa/catalog.bin` before loading;
  a repacked bundle with a stale CRC makes the game **hang on a black screen**
  ("CRC Mismatch ... Will not load AssetBundle" in `Player.log`). Any mod that
  replaces a bundle must also zero that bundle's CRC — reuse
  [`bag-capacity/patch_catalog_null.py`](bag-capacity/patch_catalog_null.py),
  which locates the bundle's record from its own **filename/GUID/size on disk**
  (never a hardcoded CRC — those change with every game update) and refuses on
  ambiguity, and register `catalog.bin` alongside the bundle
  so revert restores both. Resolve bundle paths by `<prefix>_*.bundle` globs for
  the same update-robustness reason (filename GUIDs also rehash on updates).
- **Ship no game files.** Bundle-editing patches run on the user's own
  bundles. Only `patch.py` and the scripts are published.
- **Externalise the numbers.** Each mod reads a `<mod>.json` config (a
  multiplier and/or explicit per-field values) so balancing needs no code edit.
  There is one **canonical live config** at
  `%LOCALAPPDATA%\LeafItAloneSafe\<mod>.json`, seeded from the repo `<mod>.json`
  template on first `apply.ps1` — that's the single file you (and any editor)
  edit. `apply.ps1` computes the targets from *CLI override → config*, and if the
  mod is already applied it calls `Revert-Mod` first, so **edit config →
  `apply.ps1` → relaunch** iterates cleanly with no stacking (see
  [`bag-capacity/`](bag-capacity/)). Always re-patch from a known vanilla
  baseline (the pristine backup), never from the already-modded live file.
- **Configs live in one canonical JSON the launcher edits.** Because managed
  in-process code is impossible here (encrypted `global-metadata.dat`), there is
  no in-process editor panel; the WPF launcher (L2) is the surface for changing a
  mod's values. `apply.ps1` reads the same canonical
  `%LOCALAPPDATA%\LeafItAloneSafe\<mod>.json` the launcher writes (seeded from the
  repo template on first apply), so the loop is **set in launcher → `apply.ps1` →
  relaunch**. The native `version.dll` overlay is banner-only and never edits a
  config. (The retired M8 in-game F8 editor and its `apply-on-exit.ps1` waiter were
  removed at L6.)
- **Do not** try to disable leaderboards / achievements / Steam within a
  content mod. The base does it. If a mod needs a gameplay-only "no scoring
  this run" flag, use in-game rules (e.g. set `leavesCount = 0` on a test
  area), not network/Steam patches.

## Planned mods

| Mod | What it changes | Bundle(s) | Field(s) |
|---|---|---|---|
| `bag-capacity/` ✅ **(shipped, M6 + config M7 + launcher L2)** | Bigger leaf bags — configurable (multiplier or per-bag), editable in the launcher | `coreassets_assets_meta_*` | `capacity` on 4 MonoBehaviours (Small=50, Med=200, Big=500, Giant=800) |
| `bag-speed/` | Faster seal & empty | `coreassets_assets_actors_*` | `sealingTime`, `emptyingDuration` on each `*LeafBag` |
| `more-leaves/` ✅ **(built L7 v1.1, pending in-game check)** | Denser levels — a multiplier on every area's spawn count, editable in the launcher | `scenes_scenes_castle_*`, `scenes_scenes_hometown_*`, `scenes_scenes_sakura_*` | `initArgs[].leavesCount` (+ the scene `leavesCount` pool) on the `Eternity.Gameplay.Leaves.Area` layout MonoBehaviour in each gameplay scene |
| `instant-seal/` | QoL: no seal delay | same as `bag-speed/` | set `sealingTime = 0.0` |
| `custom-textures/` | Bag / tool skin swap | `visuals_assets_bags_*`, `visuals_assets_tools_*` | Texture2D replacement (UnityPy export → edit → import) |

## Requirements
- Windows x64, *Leaf it Alone* on Steam (you must own the game).
- [`safe-base/`](../safe-base/README.md) installed and active.
- [Python 3.11 or 3.12](https://python.org) with `pip install UnityPy`.
  (Python 3.14 currently segfaults on UnityPy's native deps — pinned to 3.12.)

## License
MIT. No game files or extracted game assets are redistributed.
