# tiny-mode — Tiny player, slow player, giant world

Makes the player smaller and slows it down by the **same factor**, so the world
reads as oversized: a half-size character that also walks at half speed makes the
grass and paths feel enormous. This is a **code mod** — instead of patching asset
bundles, it ships a small managed DLL that the mod **loader** hosts, and it drives
the effect with runtime method detours owned by the loader.

**Requires the mod loader** (`version.dll` + `LiaLoader.dll` + `LiaLoader.Api.dll`)
installed — the launcher's "Install loader + safe-base" step drops those into the
game folder. Without the loader the DLL is inert and the game stays vanilla.

## Settings

The launcher shows a **Player scale** slider (0.10x – 0.50x, default 0.5x). Move
it, press Save, and the value is written to your live config file at
`%LOCALAPPDATA%\LeafItAloneSafe\tiny-mode.json`. The mod reads it on the next
launch.

- **Walk speed is tied to scale.** At 0.5x you move at half speed; the slider is
  one knob, not two.
- **Beyond the slider is allowed.** The slider stops at the supported range, but
  any value in the config file is honored. Hand-edit `tiny-mode.json` to `0.08`
  for a truly tiny player, or `1.5` to grow, and it works with no rebuild. The
  only guard is a sanity window (0.05 – 4.0) so a typo can't make you vanish.
- Code mods install/remove by toggling in the Mods tab (a file copy); config edits
  take effect on the **next launch**, not mid-session.

## How it works

The mod references **only** `LiaLoader.Api.dll` and implements `Lia.IMod`. The
loader discovers it from `<GameDir>\Mods\tiny-mode\` at boot and resolves every
game method by identity (no hardcoded RVA or token), so it survives game updates.

There is no API in this build that hands you the player or its settings object, so
the mod **captures** them the way the loader's other detour mods do: it arms
pass-through hooks on per-frame getters and reads the live receiver back from the
game thread's stub (`HookHandle.LastSelf`).

- Scale: `PlayerBehaviour.get_IsAwaitingSelection` / `get_IsCrouching` give the
  player; the mod then hops `get_transform` → a direct `Transform.set_localScale`.
- Speed: `PlayerBehaviourSettings.get_MoveSpeed` gives the settings object. That
  getter is verified to be a plain field read (`movss xmm0,[rcx+24h]`), and the
  mod writes the backing float at `+0x24` to `vanilla speed × scale`.

All three hooks always **pass through** — the mod never lies to a getter; it only
writes a transform and a field. (`get_IsSprinting` was rejected as a capture point
because sprinting is locked behind progression and is never polled early.)

## Files

| File | Purpose |
|---|---|
| `mod.json` | Launcher manifest; `dll` marks it a code mod, `config.fields` drives the slider, `configPath` is where your choice is stored. |
| `tiny-mode.dll` | The hosted code mod, built against `LiaLoader.Api` only. |

## Safety

Safe to delete: the loader hosts the game without it, and `Mods\tiny-mode\` can be
removed while the game is closed. No game files are modified — the scale and speed
are runtime writes, and Unity does not reset them for you, so the player is left at
the size and speed it was last set to.
