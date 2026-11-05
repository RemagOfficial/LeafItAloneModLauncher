# `safe-base/` — Offline-safety framework

**Status: designed, not implemented yet.** Roadmap at the bottom.

The **one thing every content mod needs** but should never have to re-do. It
guarantees that a modded session never reaches the game's online leaderboards
or Steam achievements, and that the user's real save file is unaffected.

Install this **before** any mod in `../content-mods/`.

## Why "just block the network" isn't enough

The game has:
- In-game **leaderboards** (`coreassets_assets_leaderboards` bundle,
  `UnityWebRequestModule.dll`) — modded runs can upload scores to the developer's
  web service.
- **Steamworks** integration (`com.rlabrecque.steamworks.net.dll`) —
  achievements and stats can queue inside the Steam client and sync later,
  from a *different process* than the game.
- Save data (probably **MemoryPack** — `MemoryPack.Unity.dll` is loaded) that
  the game can re-submit or re-sync on the next online launch.

So an average user who installs a content mod, uninstalls it, unblocks the
network, and plays vanilla on the same save **can still leak modded scores
days later**. Blocking alone isn't a guarantee.

## What `safe-base/` does

On install, `install.ps1` (self-elevating):

1. Adds a Windows Firewall outbound-block rule on `Leaf it Alone.exe` for all
   profiles (TCP + UDP), then **verifies** the block by attempting a real
   outbound `connect(1.1.1.1:443)` with a 1-second timeout — if it *succeeds*,
   install aborts and rolls back. This is the "you can't accidentally skip it"
   step.
2. Drops a `steam_api64.dll` proxy into the game folder that makes
   `SteamAPI_Init` fail. The game thinks Steam isn't running, so no
   achievements or Steam leaderboards record anything, ever, while modded.
3. **Isolates the save**: moves the real `UserData/` and any
   `AppData\LocalLow\*\Leaf it Alone*` to a timestamped backup, then creates a
   fresh empty `UserData/`. Modded play can't touch the user's actual
   progression.
4. Drops a `version.dll` overlay (built from the same code family as
   `../overlay/leaf_overlay.cpp`) that:
   - Re-checks all three preconditions at startup **and** every ~30 s,
     plus on hotkey press.
   - **Refuses to draw anything** if any precondition fails; a Windows
     MessageBox explains which one broke and how to fix it.
   - Draws a persistent banner:
     `OFFLINE · MODDED PLAY · SCORES WILL NOT BE SUBMITTED`.
5. Writes `%LOCALAPPDATA%\LeafItAloneSafe\manifest.json` (install timestamp,
   backup paths, sentinel hash, list of applied content mods) and
   `base.psm1` — the SDK content mods import.

On uninstall, `uninstall.ps1`:

1. Refuses if any content mods are still registered. `uninstall-all.ps1`
   reverts them first, then does this.
2. **Discards** the modded `UserData/` (with a `--keep-modded-save` escape
   hatch that also warns about the leak risk).
3. Restores the vanilla save.
4. Removes `version.dll`, `steam_api64.dll`, `LeafOverlay.ini`.
5. Removes the firewall rule. **Last.**

The mod `version.dll` **cannot run without all the surrounding safety
pieces** — so if the user manually drops files in and out of order, the mod
just doesn't activate. The framework is not a set of independent scripts
someone can half-apply.

## SDK content mods use (`base.psm1`)

```powershell
Assert-SafeBaseActive      # throws "install safe-base first" if the framework isn't present
Assert-SafePreconditions   # re-verify firewall + steam-proxy + save isolation, right now
Backup-Original <path>     # snapshot into %LOCALAPPDATA%\LeafItAloneSafe\mod-backups\<modname>\
Register-Mod -Name X -Files @(...)
Unregister-Mod -Name X
```

Content mods import it via `%LOCALAPPDATA%\LeafItAloneSafe\base.psm1` — no
need to duplicate any safety code.

## Commands

| Command | What it does |
|---|---|
| `install.ps1` | Do all the steps above. Self-elevates. |
| `uninstall.ps1` | Full revert. Refuses if content mods are still applied. |
| `uninstall-all.ps1` | Revert every registered content mod, then uninstall. |
| `status.ps1` | What's active, whether preconditions currently pass, list of applied mods. |
| `recheck.ps1` | Re-run the precondition probe without touching anything (useful if you suspect the firewall rule was cleared). |

## Roadmap

Delivered one milestone at a time, each independently testable before the next.

- **M1** — `install.ps1` / `uninstall.ps1` / `status.ps1` + firewall rule with
  verification probe. Standalone (no DLL, no save isolation yet).
- **M2** — `steam_api64.dll` proxy. Verify in-game that Steam integration
  fails gracefully but the game still launches and plays.
- **M3** — Save isolation in install/uninstall, with a real rollback test.
- **M4** — `version.dll` with preconditions + banner. Verify the trip-wire
  (delete the firewall rule mid-session, see the mod go inert).
- **M5** — `base.psm1` SDK + registry + `uninstall-all.ps1` + `recheck.ps1`.
- **M6** — First content mod (`bag-capacity`) as the SDK's real test.

## Not shipped yet
Nothing in this folder is implemented. Until M1-M5 land, use `../overlay/`
(cosmetic only, does not touch gameplay, and therefore does not need the
safety framework).

## Requirements (once implemented)
- Windows x64, *Leaf it Alone* on Steam (you must own the game).
- The game in **borderless / windowed** mode.
- Admin PowerShell for the firewall rule (self-elevates via `Start-Process -Verb RunAs`).
