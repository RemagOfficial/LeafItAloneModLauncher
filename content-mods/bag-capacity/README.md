# bag-capacity — bigger leaf bags

The first content mod shipped on top of [`safe-base`](../../safe-base/README.md).
It **doubles every leaf-bag capacity**:

| Bag | Vanilla | With this mod |
|---|---:|---:|
| Small  |  50 | **100** |
| Medium | 200 | **400** |
| Big    | 500 | **1000** |
| Giant  | 800 | **1600** |

Fewer trips to the bin, more raking. Pass `-Scale` to `apply.ps1` for a
different multiplier (e.g. `-Scale 4`).

## What it actually changes

Leaf it Alone keeps bag capacities as four `MonoBehaviour` assets named
`SmallBagCapacity` / `MediumBagCapacity` / `BigBagCapacity` / `GiantBagCapacity`,
each with a single `capacity` int, living in one small metadata bundle:

```
<game>\Leaf it Alone_Data\StreamingAssets\aa\StandaloneWindows64\
    coreassets_assets_meta_ebdcc9c18e1c4d94460e79b368e85abe.bundle
```

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
`patch_catalog.py`, which nulls *this one bundle's* CRC in the catalog
(`crc = 0` tells Unity to skip the check) — guarded so it only fires on a unique
match whose adjacent field is the vanilla bundle size. Both the bundle and the
catalog are backed up and restored together, so `revert.ps1` puts everything
back exactly as it was.

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
.\apply.ps1                 # previews, asks you to type YES
# or unattended:  .\apply.ps1 -Scale 2 -Yes
```

`apply.ps1` will:
1. refuse if the game is running or safe-base isn't installed/healthy;
2. run the patcher into a temp staging folder (the game is untouched here);
3. take a **pristine backup** of the live bundle through the SDK;
4. replace the live bundle with the patched one, then **re-read the live file**
   to prove the applied bytes are correct — if that check fails it restores the
   backup automatically;
5. register the mod with safe-base so it can be undone.

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
  --in "<game>\Leaf it Alone_Data\StreamingAssets\aa\StandaloneWindows64\coreassets_assets_meta_ebdcc9c18e1c4d94460e79b368e85abe.bundle"
```

## Known risks / troubleshooting

- **Steam "verify integrity of game files"** will re-download the original
  bundle **and** `catalog.bin` and silently undo the mod. That's harmless — just
  run `apply.ps1` again (safe-base still guards you the whole time).
- **Addressables CRC** is handled automatically (see above). If a future game
  update changes the bundle's vanilla CRC, `patch_catalog.py` refuses (the anchor
  isn't found) and `apply.ps1` restores both files — you'd just update the
  `-VanillaCrc` value to the new one from a fresh log line.
- **Game updated** → the bundle's filename hash may change and `apply.ps1` will
  report "target bundle not found". Re-check the path in
  `StreamingAssets\aa\StandaloneWindows64\` and pass it with `-BundlePath`, or
  wait for an updated mod.
- If a *different* bag mod already changed the values, `apply.ps1` refuses
  (it only patches the known vanilla numbers) — revert that one first.

## Files

| File | Role |
|---|---|
| `apply.ps1`   | installer: safety asserts → stage → backup bundle+catalog → null catalog CRC → apply → verify → register |
| `revert.ps1`  | undo via the safe-base registry (byte-identical restore of both files) |
| `patch_bag_capacity.py` | the UnityPy editor (self-verifying, refuses non-vanilla input) |
| `patch_catalog.py` | nulls this bundle's CRC in `aa/catalog.bin` (guarded: unique match + adjacent size) |

## License

MIT. This mod ships **no game files** — it patches bundles already on your own
drive and stores your untouched originals locally so it is always reversible.
