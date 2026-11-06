# Sakura petals everywhere

Brings the sakura map's special **petal leaves** into the other worlds. Choose
Hometown, the Castle, or both — each keeps its own leaf counts and scatter, only
the leaf **material** changes to the cherry-petal one.

## What it changes (and what it doesn't)

The sakura map's "petals" are **not particles and not the `petalsort`
minigame** — they are the normal GPU-instanced leaves with a different
**material**. Each world has a scene-definition asset (a MonoBehaviour named
`Hometown` / `Castle` / `Sakura`) in the coreassets **scenes** bundle, and each
holds a `[SerializeReference]` list entry of class **`LeafSettingsSceneProperty`**
(`Eternity.Application.Leaves`) with:

| field         | value |
|---------------|-------|
| `computeShader` | `LeavesComputeShader` — shared by all worlds |
| `mesh`          | `Plane` — shared by all worlds |
| `material`      | Hometown / Castle → `Leaves` (pid `-4751811775384957180`)<br>Sakura → `CherryLeaves` (pid `4121393598532695707`) |

Both materials live in `visuals_assets_leaves` (PPtr `m_FileID 5`), share one
shader and differ only by their albedo + normal textures — so swapping the
`material` path_id is *exactly* what turns a world's leaves into petals.

This mod repoints the chosen worlds' `material` at **CherryLeaves**. Leaf
counts, scatter positions and bounds are untouched (they live per-area on the
`LeafArea` actors / `InitialLeaves` banks), and the leaves stay fully pickable —
only the look changes.

> **v2 note:** v1 repointed the `LeafArea` actors' `prefabAssetReference` GUID in
> the **actors** bundle instead. That applied cleanly but changed nothing in-game
> — the GUID is not the appearance lever. v2 reverts any v1 install automatically
> on apply, then patches the scenes asset found by a whole-corpus reference scan
> (only these three MonoBehaviours reference the Leaves/CherryLeaves materials).

## How to use it

Through the launcher: **Mods → Sakura petals everywhere → toggle the worlds →
Apply & Launch.** Or by hand:

```powershell
# convert both worlds (the default from config)
.\apply.ps1 -Yes

# only Hometown
.\apply.ps1 -Hometown -Yes

# both explicitly, ignoring the config
.\apply.ps1 -All -Yes
```

Re-running `apply.ps1` reverts to the prior state first and re-patches from the
pristine backups, so tweaking the toggles and re-applying is a clean loop. Undo
with:

```powershell
.\revert.ps1 -Yes
```

## Config

The live config is `%LOCALAPPDATA%\LeafItAloneSafe\sakura-petals-everywhere.json`
(the same file the launcher writes; seeded from
[`sakura-petals-everywhere.json`](sakura-petals-everywhere.json) on first run):

```json
{ "hometown": true, "castle": true }
```

CLI switches (`-All`, `-Hometown`, `-Castle`) override the file for one run.

## Requirements & safety

- safe-base installed, game closed, and Python 3.12 with UnityPy
  (`C:\Python312\python.exe`). `apply.ps1` refuses unless all are healthy
  (`Assert-SafePreconditions`).
- It patches
  `coreassets_assets_scenes_5f42bdb8742040be8b16c59257bf7f31.bundle` and nulls
  that bundle's Addressables CRC (`fc983395`, vanilla size `8754`) in
  `catalog.bin` so the game loads the repack, backing up **both** files first
  and rolling back on any failure.
- The patcher refuses to run unless the bundle matches vanilla expectations
  (Sakura already `CherryLeaves` as the anchor; targeted worlds still `Leaves`)
  and re-opens its own output to verify the swap before the live bundle is
  replaced — so it can't double-apply or silently corrupt.

## Co-exists with the other content mods

`faster-bag-tying` patches the **actors** bundle and `bag-capacity` the **meta**
bundle — different catalog records — so this mod's CRC nullify can't clash with
them. The nullify step is still idempotent (`--allow-absent`) for re-runs.
Revert in **last-in-first-out** order for the cleanest undo.

## Files

- `patch_sakura_petals.py` — the UnityPy editor (validates vanilla materials, self-verifies output).
- `patch_catalog.py` — nulls this bundle's CRC in `catalog.bin` (idempotent).
- `apply.ps1` / `revert.ps1` — safe-base-gated apply and undo.
- `mod.json` — launcher manifest (builds the Mods UI; no launcher code changes).
- `sakura-petals-everywhere.json` — config template.

## Verification done offline (before any in-game test)

Read-only against the game; writes only into a throwaway workspace dir:

- `--verify-only` on the live scenes bundle prints the three world materials
  (Hometown/Castle = `Leaves`, Sakura = `CherryLeaves`) — exit 0.
- `--all` swaps both worlds to `CherryLeaves`, self-verifies (`verified: true`)
  and proves Sakura's own entry is untouched.
- `--hometown` converts only Hometown and proves Castle stays `Leaves`.
- **Refusal guard:** re-feeding an already-converted bundle exits non-zero
  ("already uses the CherryLeaves material").
- `patch_catalog.py --crc fc983395 --expect-size 8754 --allow-absent` anchors
  the scenes bundle's catalog record uniquely.
