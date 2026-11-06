# more-leaves — denser leaf areas

Scales the **leaf spawn counts baked into each gameplay scene** so every area
starts with more leaves to rake before you have to unload. It is the second
content mod and the L7 proof that a new UnityPy mod is added **purely from its
`mod.json` manifest** — no launcher code changes.

**Status: v1.1, built and verified offline; awaiting the in-game check** (branch
`launcher-L7`). It depends on [`../../safe-base/`](../../safe-base/README.md)
and Python 3.12 + UnityPy (the launcher's Dashboard shows a *Set up Python…*
guide if they're missing — see the L7 Python-health probe).

## The data model (and a wrong turn)

**v1.0 targeted the wrong field.** The `coreassets_assets_areas*` bundles define
each area as a `MonoBehaviour` with a top-level `leavesCount` — so the first cut
doubled those. It loaded cleanly but **nothing changed on screen** (confirmed
in game: 4× on disk, identical leaves). Those values are metadata, not the
spawner.

**The real lever lives in the scene bundles.** Each gameplay scene carries a
`Eternity.Gameplay.Leaves.Area` layout `MonoBehaviour` whose typetree has:

| Field | Meaning |
|---|---|
| `leavesCount` (top level) | the scene's leaf **pool / budget** (renderer buffer) |
| `initArgs[]` | one entry per area: `{ excludeFromInit, layoutArea (ref), leavesCount }` — how many leaves are scattered into that area at load |

Recon proved the invariant **`leavesCount == sum(initArgs[].leavesCount)`** in
all four scenes, so v1.1 scales every included area's `leavesCount` and
recomputes the scene total to the new sum (grow the pool with the spawn budget or
the extra leaves clamp). `layoutArea` refs are opaque scene-internal IDs, so this
mod is a single global **multiplier** rather than named per-area values.

| World | Scene bundle | Areas | vanilla `leavesCount` pool |
|---|---|---|---|
| Castle | `scenes_scenes_castle_5c96ecbc…` | 14 | 140288 |
| Hometown | `scenes_scenes_hometown_2afce141…` | 13 | 102400 |
| Sakura | `scenes_scenes_sakura_70c027ae…` | 12 | 260096 |

(The `scenes_scenes_menu` title-screen scene — 102400 — is left alone, and the
`coreprefabs_assets_actor` copy of the layout component is a prefab template with
its `layoutArea` refs stripped to `0`, so neither is touched.)

## Requirements
- Windows x64, *Leaf it Alone* on Steam (you must own the game).
- [`safe-base/`](../../safe-base/README.md) installed and active.
- Python **3.12** (amd64) with `python -m pip install UnityPy`. (3.14 segfaults
  on UnityPy's native lz4/brotli deps.) The launcher probes for a working
  interpreter and pops a guided setup window with the download link + the exact
  pip command if it can't find one — you don't have to guess.

## How to use
1. Install / verify **safe-base**.
2. In the launcher's Mods tab, enable **More leaves per area** and set the
   **Leaf density multiplier** (1.0 = vanilla, 2.0 = twice as dense, default 2.0).
3. **Apply & Launch**. To edit by hand instead, change
   `%LOCALAPPDATA%\LeafItAloneSafe\more-leaves.json` (seeded from
   [`more-leaves.json`](more-leaves.json) on first apply) and re-apply — `apply.ps1`
   reverts to a pristine baseline first, so there's no stacking.

Command line, without the launcher:
```powershell
.\apply.ps1 -Scale 2.5 -Yes   # 2.5x everywhere
.\apply.ps1 -Yes              # use the config file's multiplier
.\revert.ps1 -Yes
```

> **Re-applying after v1.0?** `apply.ps1` sees the old `more-leaves` registry
> entry, `Revert-Mod`s it (restoring the untouched areas bundles + catalog to
> vanilla), then patches the scenes — so it cleans up the earlier no-op mod for you.

## How it works (safety)
`apply.ps1` is the authority, mirroring [`../bag-capacity/`](../bag-capacity/README.md):
1. Import the deployed safe-base SDK; refuse if the game is running.
2. `Assert-SafeBaseActive` + `Assert-SafePreconditions` (firewall, Steam proxy,
   save isolation, banner all healthy *now*).
3. For each of the three scene bundles: patch a staged copy with UnityPy
   (`patch_leaves.py`), which refuses a bundle with no layout component and
   re-opens its own output to verify every area *and* the sum invariant before
   exiting 0.
4. `Backup-Original` every touched bundle **and** `aa/catalog.bin` (pristine), then
   null each bundle's CRC in the catalog (`patch_catalog_null.py` locates each
   record from the bundle's own filename/GUID on disk — no hardcoded CRC or
   hash, so game updates can't break it) so Addressables loads the repacks
   instead of hanging on a CRC mismatch, overwrite the bundles, re-verify the
   live files, and `Register-Mod` every file. Any failure after the backups
   restores them all. Every bundle is resolved by a `<prefix>_*.bundle` glob
   (preferring the file the current catalog references), so an update that
   rehashes filename GUIDs needs no mod change.

The scene bundles are large (castle ~80 MB, sakura ~287 MB); UnityPy repacks them
**faithfully** — an offline round-trip of the hometown scene preserved all 6626
objects with zero loss — and fast (sakura patches in ~10 s). So an apply takes
tens of seconds total. That is normal for a one-time, fully reversible write.

Because it edits real bundles it also edits real game files — always through the
safe-base backups, so `revert.ps1` / `safe-base\uninstall-all.ps1` undo everything.
Steam "verify integrity of game files" restores the bundles + catalog; just
re-run `apply.ps1` afterwards.

## To confirm in game
Apply at 2×, launch, and rake: the areas should hold visibly more leaves. If even
the scene `initArgs` change is still invisible, the next candidate is the
`initialLeaves` `TextAsset` each layout references (a distribution mask) — record
the result in `MODLOG.md` either way.

## Files
| File | Purpose |
|---|---|
| `mod.json` | Launcher manifest (describes the mod; discovery needs no code). |
| `more-leaves.json` | Repo config template (seeded to `%LOCALAPPDATA%`). |
| `apply.ps1` | Gated installer: patches the 3 scene bundles + nulls their catalog CRCs, backs up, registers, rolls back on failure. |
| `revert.ps1` | Restores the pristine scene bundles + catalog via the SDK. |
| `patch_leaves.py` | UnityPy editor: scale each area's `initArgs` count + recompute the scene pool, self-verifying. |
| `patch_catalog_null.py` | Nulls one bundle's catalog CRC, locating the record by the bundle's filename/GUID/size (update-robust, idempotent). |

## License
MIT. No game files or extracted game assets are redistributed — the patchers run
on your own bundles.
