# blower-suction-all-maps — Leaf Blower suction on every map

Makes the leaf blower's **SUCTION** mode available by default on every map except
Sakura, where it stays behind its normal purchase. This is a **code mod**: instead of
patching asset bundles like the data mods, it ships a small managed DLL that the
mod **loader** hosts, and it overrides `LeafBlowerBehaviour.get_CanToggleMode` with a
per-map decision computed off the game thread.

**Requires the mod loader** (`version.dll` + `LiaLoader.dll` + `LiaLoader.Api.dll`)
installed — the launcher's "Install loader + safe-base" step drops those into the game
folder. Without the loader the DLL is inert and the game stays vanilla.

## How it works

The mod references **only** the loader's public API assembly (`LiaLoader.Api.dll`) and
implements `Lia.IMod`. The loader discovers it from `<GameDir>\Mods\blower-suction-all-maps\`
at boot, loads it into its own collectible `AssemblyLoadContext`, and resolves the
target method by identity through `Lia.Game` (no hardcoded RVA or metadata token, so it
survives game updates). Its reference to `Lia.IMod` binds to the same type the loader
already holds, which is why no copy of `LiaLoader.Api.dll` travels with the mod — shipping
one would give it a second, incompatible type and break `IMod`.

The current map is read via `MainContext.get_ActiveScene -> get_name`, which the live
probe confirmed returns `Sakura` / `Castle` / `Hometown`. `sakuraScene` is pinned to
`Sakura`, so `except-sakura` is live: suction is forced on in Castle/Hometown and still
bought in Sakura.

## How to use

1. Install / verify **the loader + safe-base** (Dashboard).
2. In the launcher's Mods tab, enable **Leaf Blower Suction, All Maps**. Enabling a code
   mod copies its folder into `<GameDir>\Mods\blower-suction-all-maps\`; disabling removes
   it. The game must be closed (the loader keeps the DLL locked while running).
3. **Launch** — the loader hosts the mod on next start. Toggling takes effect on the next
   launch.

To make it apply suction on *every* map including Sakura, set `"mode": "all-maps"` in
`mod.json` (that variant needs no map detection and works as-is).

## Files

| File | Purpose |
|---|---|
| `mod.json` | Launcher manifest; its `dll` field marks this as a code mod and names the hosted assembly. |
| `blower-suction-all-maps.dll` | The hosted code mod. Built from the loader repo against `LiaLoader.Api` only. |

## Safety

Safe to delete: the loader hosts the game without it, and `Mods\blower-suction-all-maps\`
can be removed while the game is closed to revert. No game files are modified — the
change is a runtime method detour owned by the loader.
