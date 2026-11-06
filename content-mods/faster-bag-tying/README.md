# Faster bag tying

Makes tying a full leaf bag **faster from the start**, by lowering the base tying
time on every bag instead of waiting to buy the in-game upgrades.

## What it changes

Leaf it Alone stores the tying time as a `sealingTime` float on each bag actor
(`SmallLeafBag` / `MediumLeafBag` / `BigLeafBag` / `GiantLeafBag`), inside their
`LeafBagBehaviourSettings`. The vanilla values are:

| Bag | Tying (`sealingTime`) | Emptying (`emptyingDuration`) |
|-----|----------------------|-------------------------------|
| Small  | 1.0 s | 0.8 s |
| Medium | 1.35 s | 1.1 s |
| Big    | 1.7 s | 1.4 s |
| Giant  | 1.8 s | 1.5 s |

The game's own "faster bag tying" upgrades are **percentage** stat modifiers
(−20 / −20 / −20 / −15 % per Castle tier) applied on top of these. This mod
lowers the **base** instead, so it stacks naturally with the upgrades and works
before you've bought them.

## How to use it

Through the launcher: **Mods → Faster bag tying → choose a fraction → Apply &
Launch.** Or by hand:

```powershell
# 2x faster tying on every bag (half the vanilla time) - the default
.\apply.ps1 -Yes

# 3x faster
.\apply.ps1 -Scale 0.333 -Yes

# also speed up emptying/dumping by the same ratio
.\apply.ps1 -Scale 0.5 -AlsoEmptying -Yes

# explicit seconds per bag
.\apply.ps1 -Small 0.4 -Medium 0.6 -Big 0.8 -Giant 0.9 -Yes
```

Re-running `apply.ps1` reverts to vanilla first and re-patches from the pristine
backups, so tweaking the config (or the launcher setting) and re-applying is a
clean loop. Undo with:

```powershell
.\revert.ps1 -Yes
```

## Config

The live config is `%LOCALAPPDATA%\LeafItAloneSafe\faster-bag-tying.json` (the
same file the launcher writes; seeded from [`faster-bag-tying.json`](faster-bag-tying.json)
on first run):

```json
{ "mode": "multiplier", "multiplier": 0.5, "alsoEmptying": false }
```

- `mode` = `multiplier` (a fraction of the vanilla time, in (0,1]) or `seconds`
  (explicit `seconds.small/medium/big/giant`).
- `alsoEmptying` = also scale the emptying/dumping time by the same per-bag ratio.

CLI flags (`-Scale`, `-Small..-Giant`, `-AlsoEmptying`) override the file for one run.

## Requirements & safety

- safe-base installed, game closed, and Python 3.12 with UnityPy
  (`C:\Python312\python.exe`). `apply.ps1` refuses unless all are healthy
  (`Assert-SafePreconditions`).
- It patches
  `coreassets_assets_actors_3f08c135ba3f70dc386f83f20db5b74e.bundle` and nulls
  that bundle's Addressables CRC (`88295665`) in `catalog.bin` so the game loads
  the repack, backing up **both** files first and rolling back on any failure.
- Everything is reversible via `revert.ps1` or `safe-base\uninstall-all.ps1`.
  Steam "verify integrity of game files" restores both; just re-run `apply.ps1`.

## Files

- `patch_faster_bag_tying.py` — the UnityPy editor (validates vanilla input, self-verifies output).
- `patch_catalog.py` — nulls this bundle's CRC in `catalog.bin`.
- `apply.ps1` / `revert.ps1` — safe-base-gated apply and undo.
- `mod.json` — launcher manifest (builds the Mods UI; no launcher code changes).
- `faster-bag-tying.json` — config template.
