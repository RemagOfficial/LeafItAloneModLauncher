# larger-leaves - physically bigger leaves

Makes every leaf you see **bigger on screen**, to any size you want, via a
config multiplier. This is the one leaf mod that is *not* a data change: the
drawn size is baked into the game's leaf render shader, so the mod rewrites a
single shader constant and repairs the shader's integrity hash.

Default doubles every leaf (`x2`).

## Why this needs a shader patch

Leaf it Alone does not read a leaf's on-screen size from any asset. The leaf
render shader (`Unlit/LeafShader`, a baked `Shader` in the
`shaders_assets_all_<guid>.bundle`) draws each leaf by reading a record out of
the `leaves` StructuredBuffer and normalising the mesh-quad corner to a *fixed*
length:

```
div r0.w, l(0.141420), r0.w      ; 0.141420 = 0.1 * sqrt(2) = the leaf radius, in world units
```

Because the corner is normalised, scaling the leaf mesh vertices or the
`leafWidths` data does nothing (both were proven in-game). The only real lever
is that baked DXBC immediate, so this mod multiplies it by your chosen
factor.

## The DXBC digest (the part that bit us once)

Unity bakes each shader program into a **DXBC container**. The 16 bytes at
container offset 4 are a *content digest* that is validated when the shader is
parsed. Editing the bytecode float without recomputing that digest makes
`D3DDisassemble` fail (`E_FAIL`) and Unity's D3D backend reject the vertex
shader (`D3D shader create error [0x80070057]`), so the leaves silently stop
drawing entirely - the carpet just vanishes.

`patch_leaf_size.py` therefore does two things on every edit:

1. swaps the size constant `0.141420 -> 0.141420 * multiplier` (a fixed-width
   4-byte swap, so all instruction offsets and the bytecode length stay put),
   and
2. recomputes each container's digest with the exact FXC algorithm (MD5's raw
   block function over `container[20:]` with a bespoke final block; implemented
   in `dxbc_digest.py` and verified to reproduce all 239 pristine container
   digests in the bundle).

The constant lives in 6 vertex/pixel programs; all 6 are patched and re-hashed.

## The Addressables wrinkle

This game loads bundles through Unity Addressables, which verifies each
bundle's CRC against `aa/catalog.bin` before loading. Repacking the bundle
changes its CRC, so on a stock catalog the game refuses it and hangs on a black
screen. `apply.ps1` runs `patch_catalog_null.py`, which nulls *this one
bundle's* CRC in the catalog (`crc = 0` = skip the check). It locates the
record from the bundle's own **filename/GUID on disk** (plus its live size),
never a hardcoded value, so a game update that rehashes the CRC cannot break
it; it refuses on ambiguity and treats an already-zeroed CRC as a no-op. The
bundle and the catalog are backed up and restored together, so `revert.ps1`
puts everything back exactly as it was.

## Requirements

- Windows x64, **Leaf it Alone** on Steam (you must own the game).
- [`safe-base`](../../safe-base/README.md) **installed and active** - this mod
  asserts the whole safety stack is healthy before it touches a byte.
- **Python 3.12** with UnityPy, at `C:\Python312\python.exe`:
  ```powershell
  C:\Python312\python.exe -m pip install UnityPy
  ```
  (Python 3.14 segfaults on UnityPy's native lz4/brotli deps. Point at a
  different interpreter with `-Python <path>` if needed.)

## Install

```powershell
# close the game first
cd content-mods\larger-leaves
.\apply.ps1                 # reads larger-leaves.json, previews, asks you to type YES
# or unattended:  .\apply.ps1 -Yes
# or one-off:     .\apply.ps1 -Scale 1.5 -Yes
```

`apply.ps1` will:
1. refuse if the game is running or safe-base isn't installed/healthy;
2. run the shader patcher (with digest repair) into a temp staging folder (the
   game is untouched here);
3. take a **pristine backup** of the live bundle and `catalog.bin`;
4. null the catalog CRC, replace the bundle, then **re-read the live file** to
   prove the applied bytes and digests are correct - if that fails it restores
   both backups automatically;
5. register the mod with safe-base so it can be undone.

## Configuration

The size comes from one **canonical config file**:

```
%LOCALAPPDATA%\LeafItAloneSafe\larger-leaves.json
```

The first time you run `apply.ps1` it seeds that file from the
[`larger-leaves.json`](larger-leaves.json) template next to the script, so
there is exactly one place to edit from then on (the launcher writes the same
file):

```jsonc
{ "multiplier": 2.0 }        // 1.0 = vanilla, 2.0 = twice as big
```

To change the size by hand: **edit that JSON, then run `apply.ps1` again.** It
notices the mod is already applied, reverts to vanilla first, re-patches from
the pristine backup with your new number, and re-registers - so iterating is
just *edit -> apply -> relaunch*. Nothing is ever stacked.

A command-line flag overrides the config for a one-off:

```powershell
.\apply.ps1 -Scale 1.5                 # x1.5 this run
.\apply.ps1 -Config D:\my\leaves.json  # point at a different config file
```

Precedence: `-Scale` > the config file.

## Uninstall

```powershell
.\revert.ps1                 # restores the vanilla shader bundle + catalog
```

Or undo *everything* - this mod plus any other content mods, then the whole
safety framework - in one shot:

```powershell
..\..\safe-base\uninstall-all.ps1
```

Revert restores the **exact original bytes**; the patched file is parked next
to it as `*.modified-<timestamp>`, never deleted.

## How to check it worked

Launch the game. The leaf carpet on the menu patio should be visibly bigger
(each leaf quad roughly `multiplier` times its vanilla size). You can also
confirm the live constant without the game:

```powershell
C:\Python312\python.exe patch_leaf_size.py --verify-only --size 2.0 `
  --in "<game>\Leaf it Alone_Data\StreamingAssets\aa\StandaloneWindows64\shaders_assets_all_<guid>.bundle"
```

## Known risks / troubleshooting

- **Leaves vanished entirely** after a patch means the DXBC digest was not
  repaired - which this version always does. If you see it anyway, the game
  updated the shader and the constant moved; re-run recon before patching.
- **Steam "verify integrity of game files"** re-downloads the original bundle
  **and** `catalog.bin` and silently undoes the mod. Harmless - run `apply.ps1`
  again.
- **Addressables CRC** is handled automatically and is update-robust (record
  located by filename/GUID). If it can't be located uniquely,
  `patch_catalog_null.py` refuses and `apply.ps1` restores both files.
- **Game updated** -> the bundle's filename hash may change; `apply.ps1` finds
  it via a `shaders_assets_all_*.bundle` glob (preferring the file the current
  catalog references). It only reports "no ... bundle found" if the install
  layout itself moved - pass `-BundlePath` then.
- If a *different* mod already changed the leaf shader constant, `apply.ps1`
  refuses (it only patches the known vanilla value) - revert that one first.

## Files

| File | Role |
|---|---|
| `larger-leaves.json` | the config **template** (a single multiplier); seeded into `%LOCALAPPDATA%\LeafItAloneSafe\larger-leaves.json` on first apply - that live copy is what you and the launcher edit |
| `mod.json` | launcher manifest: describes the mod and its one `multiplier` config field (data-driven UI, no launcher code change) |
| `apply.ps1`   | installer: safety asserts -> resolve multiplier -> (revert if already applied) -> stage -> backup bundle+catalog -> null catalog CRC -> apply -> verify -> register |
| `revert.ps1`  | undo via the safe-base registry (byte-identical restore of both files) |
| `patch_leaf_size.py` | the UnityPy shader editor: swaps the size constant, repairs every DXBC digest, self-verifies, refuses non-vanilla input |
| `dxbc_digest.py` | the FXC DXBC container digest (pure-Python MD5 block function); imported by the patcher |
| `patch_catalog_null.py` | nulls this bundle's CRC in `aa/catalog.bin`, locating the record by the bundle's filename/GUID/size (update-robust, idempotent, refuses on ambiguity) |

## License

MIT. This mod ships **no game files** - it patches a bundle already on your own
drive and stores your untouched originals locally so it is always reversible.
