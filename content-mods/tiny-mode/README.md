# tiny-mode — Tiny player, slow jumps, giant world

Makes the player smaller and scales its **walk speed and jump height** by the
**same factor**, so the world reads as oversized: a half-size character that also
walks at half speed and leaps to half height makes the grass, paths and doorways
feel enormous. This is a **code mod** — instead of patching asset
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

- **Walk speed and jump height are tied to scale.** At 0.5x you move at half speed
  and jump to half height; the slider is one knob, not three. (Jump *height* is
  scaled linearly with size, which means the jump *impulse* is scaled by the square
  root — half the launch speed is a quarter the raw height, so the game's ballistic
  `h = v²/2g` lands you back at half height.)
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
- Speed: the mover (`PlayerBehaviour.HandleMovement`) computes the player's effective speed
  from the player's **own** `moveSpeed` field at `PlayerBehaviour+0xF0` — not a float, but a
  `CompoundFloat` (base value at `+0x10`, recomputed cache at `+0x48`). The mod reads that
  object off the player it already captured, latches the vanilla base once, and every beat
  writes `base × scale` to BOTH the base and the cache — the mover multiplies by the cache,
  and the game recomputes it event-driven, so anything less loses the race and walking
  creeps back to full speed. (Trade-off: while the mod is on, the game's own speed bonuses
  like sprint are over-asserted; half size = half speed always holds.) Verified live:
  holding the cache at 0.5 made a 3-second walk cross ~1.5 m where full speed crosses a
  whole zone.
  (An earlier theory — a settings instance behind `get_MoveSpeed` at `+0x24` — was disproved:
  poking it left walking at full speed.)
- Jump: the jump impulse is `PlayerBehaviourSettings.jumpForce`, a plain float at
  `PlayerBehaviourSettings+0x28` (vanilla 3.5). The per-player settings object hangs off
  the player at `PlayerBehaviour+0x58` (the `+0xC0` field is the *global* `GameSettings`, a
  different object). Both jump read sites — `TryJump` and `HandleJump` — consult that one
  field, so a direct write lowers the jump with no `min()` race (unlike the old native
  instruction-patch boost, which had to patch both sites). Because jump height is ballistic
  (`h ~ v²`), the mod scales the impulse by `sqrt(scale)` so **height** scales by `scale`:
  half size → 0.707x force → 0.5x height. Re-asserted every beat so a map reload is
  immediately re-scaled. Verified live: writing 20.0 to `+0x28` made the player rocket
  upward; 3.5 restored the normal jump.

Both hooks always **pass through** — the mod never lies to a getter; it only
writes a transform and a field. (`get_IsSprinting` was rejected as a capture point
because sprinting is locked behind progression and is never polled early.)

## Files

| File | Purpose |
|---|---|
| `mod.json` | Launcher manifest; `dll` marks it a code mod, `config.fields` drives the slider, `configPath` is where your choice is stored. |
| `tiny-mode.dll` | The hosted code mod, built against `LiaLoader.Api` only. |

## Safety

Safe to delete: the loader hosts the game without it, and `Mods\tiny-mode\` can be
removed while the game is closed. No game files are modified — the scale, speed and
jump force are runtime writes, and Unity does not reset them for you, so the player is
left at the size, speed and jump it was last set to.
