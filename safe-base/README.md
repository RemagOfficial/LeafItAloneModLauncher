# `safe-base/` — Offline-safety framework

**Status: M1 landed — a real Windows Firewall outbound-block with verification
is implemented and testable.** M2-M6 (Steam proxy, save isolation, DLL
trip-wire, PS SDK, first content mod) are still planned; roadmap at the bottom.

Install this **before** any mod in `../content-mods/`.

## Quick start (M1)

```powershell
# Adds a Windows Firewall OUTBOUND-BLOCK rule for Leaf it Alone.exe.
# Self-elevates via UAC; asks for confirmation ("YES") first.
.\safe-base\install.ps1

# Read-only report: rule, program, profile, Firewall service, manifest.
.\safe-base\status.ps1

# Removes the rule + manifest. Refuses to touch anything if content mods are
# still registered with the base (that check kicks in from M5).
.\safe-base\uninstall.ps1
```

Override the auto-detected game path with
`.\safe-base\install.ps1 -GameExe "D:\some\other\Leaf it Alone.exe"`.

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

*(Below is the full framework design. **M1 = step 1 only.** The other
steps arrive as M2-M5 land; each milestone is independently testable.)*

On install, `install.ps1` (self-elevating):

1. **[M1 landed]** Adds a Windows Firewall outbound-block rule on
   `Leaf it Alone.exe` for all profiles (TCP + UDP), then **verifies** the rule
   exists / is enabled / has the right action / direction / profile, and that
   the Windows Firewall service (`MpsSvc`) is running. The install aborts on
   any verification failure. Writes an install manifest at
   `%LOCALAPPDATA%\LeafItAloneSafe\manifest.json`.
   - The live outbound `connect(1.1.1.1:443)` probe that a PowerShell script
     can't perform (firewall rules only affect the target program's traffic)
     ships at **M4** inside the `version.dll` mod, where the probe runs
     *from the game process itself*.
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

| Command | Status | What it does |
|---|---|---|
| `install.ps1` | **M1 landed** | Add firewall rule + verify. Self-elevates. |
| `uninstall.ps1` | **M1 landed** | Remove firewall rule + manifest. Refuses if content mods are still applied (guard is live but registry fills up in M5). |
| `status.ps1` | **M1 landed** | Read-only: rule present, service running, manifest, registered mods. No admin required. |
| `common.ps1` | shared | Dot-sourced helpers: `Find-GameExe`, `Test-IsAdmin`, `Get-FirewallState`. Constants: rule name, display, description, state dir, manifest path. |
| `uninstall-all.ps1` | M5 | Revert every registered content mod, then uninstall. |
| `recheck.ps1` | M4 | Re-run the precondition probe from inside the game process. |
| `base.psm1` | M5 | SDK content mods import. |

## Roadmap

Delivered one milestone at a time, each independently testable before the next.

- **M1 - LANDED, TESTED** — `install.ps1` / `uninstall.ps1` / `status.ps1` +
  `common.ps1`. Firewall rule + verification (rule enabled/action/direction/
  profile + `MpsSvc` service running) + manifest. Standalone: no DLL, no save
  isolation, no Steam proxy yet. Verified end-to-end 2026-11-05 (see
  "Leak paths" section below for what M1 does and doesn't stop). Test plan:
  1. `install.ps1` -> accept the plan, approve UAC.
  2. `status.ps1` -> should show the rule present, `MpsSvc = Running`,
     manifest path, "VERDICT: safe-base M1 active".
  3. Open `wf.msc` (Windows Firewall with Advanced Security) -> Outbound
     Rules -> find `Leaf it Alone Safe (block outbound - modding)` ->
     General tab -> Action = Block, Programs = Leaf it Alone.exe.
  4. Launch the game normally. Steam still opens it (loopback IPC in a
     separate process, unaffected by our rule). Inside the game:
     - The game's **direct HTTPS** calls (UnityWebRequest to the dev's own
       service, if it has one) will time out or fail.
     - **Steamworks-based content (Steam friends, Steam leaderboards, Steam
       cloud saves)** still works, because the actual network calls happen
       inside `Steam.exe`, a different process our rule doesn't target.
       That's expected at M1; **M2** closes it.
  5. `uninstall.ps1` -> confirm -> rule gone, `status.ps1` shows "not
     installed".
- **M2** — `steam_api64.dll` proxy. Verify in-game that Steam integration
  fails gracefully but the game still launches and plays.
- **M3** — Save isolation in install/uninstall, with a real rollback test.
- **M4** — `version.dll` with preconditions + banner + the live
  `connect(1.1.1.1:443)` outbound probe (only meaningful from the game's own
  process). Verify the trip-wire (delete the firewall rule mid-session, see
  the mod go inert).
- **M5** — `base.psm1` SDK + registry + `uninstall-all.ps1` + `recheck.ps1`.
- **M6** — First content mod (`bag-capacity`) as the SDK's real test.

## Leak paths — what M1 blocks vs what's left

Leaf it Alone ships both `com.rlabrecque.steamworks.net.dll` (Steamworks) and
`UnityWebRequestModule.dll` (the game's own HTTP client). That means there are
**two distinct upload paths** and they need **different** mitigations.

| Leak path | How it works | Blocked by M1? | Milestone that closes it |
|---|---|---|---|
| Game → its own web service (HTTPS via `UnityWebRequest`) | `Leaf it Alone.exe` opens a direct TCP+TLS connection to a dev-hosted endpoint | ✅ **Yes** — outbound SYN blocked, connection never establishes | M1 |
| Game → Steam leaderboard upload (`ISteamUserStats::UploadLeaderboardScore`) | Game calls the API in `steam_api64.dll`; that call goes over **loopback IPC** into `Steam.exe`; Steam.exe makes the actual HTTPS call to `steampowered.com` | ❌ **No** — loopback isn't filtered by an exe-scoped rule, and the SYN comes from `Steam.exe`, not `Leaf it Alone.exe` | **M2** (`steam_api64.dll` proxy makes `SteamAPI_Init` fail; game never even gets to call upload) |
| Game → Steam stats / achievements (`SetStat`, `SetAchievement`) | Same Steamworks IPC as above; Steam client queues + syncs | ❌ **No** | M2 |
| Steam Cloud save sync | Steam pushes / pulls `UserData/` files on its own schedule | ❌ **No** | M3 (save isolation) + M2 (never enter the online state that queues a sync) |
| Real save replayed on the next online launch (queued or embedded submissions) | Uninstall firewall → play vanilla → the game (or Steam) resubmits anything the save says it should | ❌ **No** | M3 (modded `UserData/` is **discarded**, vanilla restored) |
| Rule silently removed while game is running (VPN client resets firewall, group policy, malware, user tinkering) | Game keeps playing but the block is gone | ❌ No — no trip-wire yet | M4 (in-DLL precondition re-check every ~30 s + live `connect()` probe + trip-wire banner) |

**The takeaway:** M1 is necessary and it does real work, but **on its own it
is not a guarantee.** A game that uses Steamworks for its leaderboards can
still upload modded scores *through the Steam client's own process*, which is
outside our exe-scoped firewall rule's reach. That's why `safe-base` needs
M2 + M3 before any content mod should be considered safe to distribute.

## What M1 does **not** do yet

- Blocks the game's own **direct** outbound traffic — ✅ **yes** (M1's job).
- Blocks anything routed through the **Steam client's** process — ❌ **no**,
  that's M2's `steam_api64.dll` proxy.
- Moves or backs up the save — ❌ **no**, M3.
- Detects the firewall rule being removed mid-session — ❌ **no**, M4.
- The `base.psm1` SDK content mods use — ❌ **no**, M5.

Until M5, no content mod is safe to install on top of just M1.
Use `../overlay/` in the meantime (cosmetic only, does not touch gameplay,
and therefore does not need the safety framework).

## Requirements (for M1)
- Windows x64, *Leaf it Alone* on Steam (you must own the game).
- Admin PowerShell for the firewall rule (self-elevates via `Start-Process -Verb RunAs`).
- Later milestones add: the game in **borderless / windowed** mode (M4
  overlay), Python 3.12 + UnityPy for content mods (M6).
