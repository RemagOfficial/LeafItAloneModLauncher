# bag-capacity — bigger leaf bags

The first content mod shipped on top of [`safe-base`](../../safe-base/README.md).
It **makes every leaf bag bigger** — to any size you want, via a config file.
The default doubles every capacity:

| Bag | Vanilla | Default (x2) |
|---|---:|---:|
| Small  |  50 | **100** |
| Medium | 200 | **400** |
| Big    | 500 | **1000** |
| Giant  | 800 | **1600** |

Fewer trips to the bin, more raking. Change the multiplier or set each bag
individually — edit the config in the launcher, or by hand (see **Configuration**
below) — no code, no re-install.

## What it actually changes

Leaf it Alone keeps bag capacities as four `MonoBehaviour` assets named
`SmallBagCapacity` / `MediumBagCapacity` / `BigBagCapacity` / `GiantBagCapacity`,
each with a single `capacity` int, living in one small metadata bundle:

```
<game>\Leaf it Alone_Data\StreamingAssets\aa\StandaloneWindows64\
    coreassets_assets_meta_ebdcc9c18e1c4d94460e79b368e85abe.bundle
```
(the `<guid>` hash in the filename changes with game updates; `apply.ps1` finds
the file by a `coreassets_assets_meta_*.bundle` glob, so the name above is just
an example of the current install)

The bundle is plain (unencrypted) UnityFS with intact TypeTrees, so
[UnityPy](https://github.com/Logardas/UnityPy) reads and rewrites the fields
directly — no C# runtime, no BepInEx/MelonLoader (both are blocked here by the
encrypted `global-metadata.dat`). `patch_bag_capacity.py` does the edit; it
refuses to run unless it finds the exact vanilla values, and it re-opens its own
output to verify the new numbers before writing anything back into the game.

**The Addressables wrinkle.** This game loads bundles through Unity
Addressables 2.9, which verifies each bundle's CRC against `aa/catalog.bin`
before loading (`AssetBundle.LoadFromFileAsync(path, crc)`). Repacking the bundle
changes its CRC, so on a stock catalog the game **refuses the bundle and hangs on
a black screen** after the logo. `apply.ps1` therefore also runs
`patch_catalog_null.py`, which nulls *this one bundle's* CRC in the catalog
(`crc = 0` tells Unity to skip the check). It locates the bundle's catalog record
from the bundle's own **filename/GUID on disk** (plus its live size as a
confirm), never a hardcoded CRC value, so a game update that rehashes the CRC
cannot break it; it refuses if the record is ambiguous and treats an
already-zeroed CRC as a no-op. The bundle it patches is found by a
`coreassets_assets_meta_*.bundle` glob (the filename hash can change with
updates). Both the bundle and the catalog are backed up and restored together,
so `revert.ps1` puts everything back exactly as it was.

## Requirements

- Windows x64, **Leaf it Alone** on Steam (you must own the game).
- [`safe-base`](../../safe-base/README.md) **installed and active** — this mod
  asserts the whole safety stack (firewall + Steam proxy + save isolation +
  banner) is healthy before it touches a single byte, so you can never mod an
  unprotected game.
- **Python 3.12** with UnityPy, at `C:\Python312\python.exe`:
  ```powershell
  C:\Python312\python.exe -m pip install UnityPy
  ```
  (Python 3.14 currently segfaults on UnityPy's native lz4/brotli deps. Point at
  a different interpreter with `-Python <path>` if needed.)

## Install

```powershell
# close the game first
cd content-mods\bag-capacity
.\apply.ps1                 # reads bag-capacity.json, previews, asks you to type YES
# or unattended:  .\apply.ps1 -Yes            (sizes from bag-capacity.json)
# or one-off:     .\apply.ps1 -Scale 4 -Yes
```

`apply.ps1` will:
1. refuse if the game is running or safe-base isn't installed/healthy;
2. run the patcher into a temp staging folder (the game is untouched here);
3. take a **pristine backup** of the live bundle through the SDK;
4. replace the live bundle with the patched one, then **re-read the live file**
   to prove the applied bytes are correct — if that check fails it restores the
   backup automatically;
5. register the mod with safe-base so it can be undone.

## Configuration

Sizes come from one **canonical config file**:

```
%LOCALAPPDATA%\LeafItAloneSafe\bag-capacity.json
```

The first time you run `apply.ps1` it seeds that file from the
[`bag-capacity.json`](bag-capacity.json) template next to the script, so there is
exactly one place to edit from then on (the repo copy is just the starting
template). Two modes:

```jsonc
// A. multiplier — scales the vanilla 50/200/500/800
{ "mode": "multiplier", "multiplier": 3.0 }        // -> 150 / 600 / 1500 / 2400

// B. sizes — set each bag exactly
{ "mode": "sizes",
  "sizes": { "small": 100, "medium": 500, "big": 1200, "giant": 3000 } }
```

To change sizes by hand: **edit that JSON, then run `apply.ps1` again.** It
notices the mod is already applied, reverts to vanilla first, re-patches from the
pristine backup with your new numbers, and re-registers — so iterating is just
*edit → apply → relaunch*. Nothing is ever stacked. (Or set the values in the
launcher, which writes the same config file.)

Command-line flags override the config for a one-off without editing the file:

```powershell
.\apply.ps1 -Scale 4                     # x4 everything, this run
.\apply.ps1 -Small 100 -Medium 500 -Big 1200 -Giant 3000   # explicit (give all four)
.\apply.ps1 -Config D:\my\bagcap.json    # point at a different config file
```

Precedence: explicit `-Small/-Medium/-Big/-Giant` > `-Scale` > the config file.

## Uninstall

```powershell
.\revert.ps1                 # restores the vanilla bundle (moves the modded one aside)
```

Or undo *everything* — this mod plus any other content mods, then the whole
safety framework — in one shot:

```powershell
..\..\safe-base\uninstall-all.ps1
```

Revert restores the **exact original bytes** (verified byte-for-byte in the
round-trip test); the patched file is parked next to it as
`*.modified-<timestamp>`, never deleted.

## How to check it worked

Launch the game and open the shop / your bags — a Small bag should hold **100**
leaves instead of 50. You can also confirm the live value any time without the
game:

```powershell
C:\Python312\python.exe patch_bag_capacity.py --verify-only `
  --in "<game>\Leaf it Alone_Data\StreamingAssets\aa\StandaloneWindows64\coreassets_assets_meta_<guid>.bundle"
```

## Known risks / troubleshooting

- **Steam "verify integrity of game files"** will re-download the original
  bundle **and** `catalog.bin` and silently undo the mod. That's harmless — just
  run `apply.ps1` again (safe-base still guards you the whole time).
- **Addressables CRC** is handled automatically and is update-robust (see
  above): the catalog record is located by the bundle's own filename/GUID, so a
  game update that rehashes the CRC needs **no mod change**. If the record ever
  can't be located uniquely, `patch_catalog_null.py` refuses and `apply.ps1`
  restores both files — read its JSON error and re-run after finishing the
  update / a Steam verify.
- **Game updated** → the bundle's filename hash may change; `apply.ps1` finds it
  automatically via a `coreassets_assets_meta_*.bundle` glob (preferring the
  file the current catalog references), so no manual step is needed. It only
  reports "no ... bundle found" if the install layout itself moved — pass
  `-BundlePath` in that case.
- If a *different* bag mod already changed the values, `apply.ps1` refuses
  (it only patches the known vanilla numbers) — revert that one first.

## Files

| File | Role |
|---|---|
| `bag-capacity.json` | the config **template** (a multiplier or explicit per-bag sizes); seeded into `%LOCALAPPDATA%\LeafItAloneSafe\bag-capacity.json` on first apply — that live copy is what you and the launcher edit |
| `apply.ps1`   | installer: safety asserts → resolve sizes → (revert if already applied) → stage → backup bundle+catalog → null catalog CRC → apply → verify → register |
| `revert.ps1`  | undo via the safe-base registry (byte-identical restore of both files) |
| `patch_bag_capacity.py` | the UnityPy editor (explicit sizes or scale; self-verifying, refuses non-vanilla input) |
| `patch_catalog_null.py` | nulls this bundle's CRC in `aa/catalog.bin`, locating the record by the bundle's filename/GUID/size (update-robust, idempotent, refuses on ambiguity) |

## License

MIT. This mod ships **no game files** — it patches bundles already on your own
drive and stores your untouched originals locally so it is always reversible.
