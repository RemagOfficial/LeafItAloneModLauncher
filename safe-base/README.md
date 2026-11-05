# `safe-base/` — Offline-safety framework

**Status: M1 + M2 landed — a verified Windows Firewall outbound-block AND a
`steam_api64.dll` stub proxy that makes `SteamAPI_Init` fail.** Together these
close every upload path the game process can initiate. M3-M6 (save isolation,
DLL trip-wire, PS SDK, first content mod) are still planned; roadmap at the
bottom.

Install this **before** any mod in `../content-mods/`.

## Quick start (M1 + M2)

```powershell
# Adds a Windows Firewall OUTBOUND-BLOCK rule for Leaf it Alone.exe, then
# replaces steam_api64.dll with a stub proxy (real one backed up). Self-
# elevates via UAC; asks for confirmation ("YES") first.
.\safe-base\install.ps1

# Read-only report: rule, program, profile, Firewall service, Steam proxy
# state (proxy in place? real restored? backup present?), manifest, mods.
.\safe-base\status.ps1

# Restores the real steam_api64.dll first, then removes the firewall rule
# LAST (so the network stays blocked until file state is vanilla again).
.\safe-base\uninstall.ps1
```

Override the auto-detected game path with
`.\safe-base\install.ps1 -GameExe "D:\some\other\Leaf it Alone.exe"`.
Skip the Steam proxy step (M1 only, useful for testing the rule in isolation)
with `.\safe-base\install.ps1 -SkipSteam`.

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

*(Below is the full framework design. **M1 + M2 = steps 1 and 2.** The other
steps arrive as M3-M5 land; each milestone is independently testable.)*

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
2. **[M2 landed]** Locates the game's real `steam_api64.dll`, moves it to
   `%LOCALAPPDATA%\LeafItAloneSafe\backup\steam_api64.dll`, and drops the
   auto-generated **stub proxy** from `steam_proxy/` in its place. Every
   Steam export returns `0` / `nullptr`, so `SteamAPI_InitFlat` (which
   `SteamAPI_Init()` wraps inline in the modern Steamworks SDK) returns
   false. Steamworks.NET never gets an `ISteamUserStats`, and the game has
   no way to call `UploadLeaderboardScore`, `SetStat`, `SetAchievement`, or
   touch Steam Cloud. If M2 fails after M1 succeeded, install rolls back
   the firewall rule so state stays coherent.
   - Override with `-SkipSteam` if you only want M1 for testing.
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
| `install.ps1` | **M1 + M2 landed** | Firewall rule + Steam proxy + verify + manifest. Self-elevates. Rolls back M1 if M2 fails. |
| `uninstall.ps1` | **M1 + M2 landed** | Restores the real `steam_api64.dll` from backup first, then removes the firewall rule LAST. Refuses if content mods are still applied (guard is live; registry fills up in M5). |
| `status.ps1` | **M1 + M2 landed** | Read-only: rule present + service running + Steam-proxy state (proxy / real / absent / unknown) + backup integrity + manifest + registered mods. No admin. |
| `common.ps1` | shared | Dot-sourced helpers: `Find-GameExe`, `Test-IsAdmin`, `Get-FirewallState`, `Find-GameSteamDll`, `Install-SteamProxy`, `Restore-SteamProxy`, `Classify-SteamDll`, `Get-FileSha256`. Constants: rule name, display, description, state dir, manifest path, backup dir, `SteamProxySource`. |
| `steam_proxy/generate_proxy.ps1` | **M2 landed** | Regenerates `steam_proxy.cpp` + `steam_proxy.def` from `exports.txt`. Deterministic; only rerun if the game's SDK changes its export set. |
| `steam_proxy/build_steam_proxy.ps1` | **M2 landed** | Compiles `steam_proxy.cpp` → `steam_api64.dll` (162 KB stub proxy). Ships prebuilt so end users don't need MSVC. |
| `steam_proxy/exports.txt` | M2 source of truth | 1089 export names captured from the game's current `steam_api64.dll`. |
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
- **M2 - LANDED** — `steam_api64.dll` stub proxy. `install.ps1` finds the
  game's real `steam_api64.dll`, moves it to `%LOCALAPPDATA%\LeafItAloneSafe\
  backup\steam_api64.dll`, drops the prebuilt proxy at the same path, and
  verifies with hash match + size classification. `uninstall.ps1` restores
  the real DLL first (refusing if what's there now isn't our proxy — so we
  never clobber a manual restore). `status.ps1` shows the current DLL class.
  Test plan:
  1. `install.ps1` -> confirm plan, approve UAC -> should print `[M2] proxy
     active` and record `steamProxy` block in the manifest.
  2. `status.ps1` -> `[M2] Steam proxy layer: current DLL: PROXY`, `backup
     present: yes`, and verdict `M1 + M2 active`.
  3. Check the file at the game's `Plugins/x86_64/steam_api64.dll`: ~162 KB,
     SHA matches the repo's `steam_proxy/steam_api64.dll`.
  4. Launch the game. If it starts and plays normally (no "Steam required"
     error, no crash), M2 works. In-game: Steam friends list, Steam
     achievements screen, in-game Steam leaderboard view — expect them to
     show as unavailable or empty, not to load. **If the game hard-refuses
     to launch without Steam**, we need a compat-mode variant that lets
     init succeed but stubs `UploadLeaderboardScore`/`SetStat` specifically.
  5. `uninstall.ps1` -> should print `[M2] Restoring real steam_api64.dll...`,
     then `[M1] Removing firewall rule...`, then the manifest. Verify the
     DLL size at the game's path is back to ~320 KB and its SHA matches the
     backup the manifest recorded.
- **M3** — Save isolation in install/uninstall, with a real rollback test.
- **M4** — `version.dll` with preconditions + banner + the live
  `connect(1.1.1.1:443)` outbound probe (only meaningful from the game's own
  process). Verify the trip-wire (delete the firewall rule mid-session, see
  the mod go inert).
- **M5** — `base.psm1` SDK + registry + `uninstall-all.ps1` + `recheck.ps1`.
- **M6** — First content mod (`bag-capacity`) as the SDK's real test.

## Leak paths — what M1 + M2 block vs what's left

Leaf it Alone ships both `com.rlabrecque.steamworks.net.dll` (Steamworks) and
`UnityWebRequestModule.dll` (the game's own HTTP client). Two upload paths,
two mitigations.

| Leak path | How it works | M1 (firewall) | M2 (proxy) | What closes it |
|---|---|---|---|---|
| Game → its own web service (HTTPS via `UnityWebRequest`) | `Leaf it Alone.exe` opens a direct TCP+TLS connection to a dev-hosted endpoint | ✅ blocked (SYN dies) | n/a | **M1** |
| Game → Steam leaderboard upload (`UploadLeaderboardScore`) | Game calls `steam_api64.dll` → loopback IPC → `Steam.exe` → HTTPS | ❌ (Steam's exe isn't the rule target; loopback unfiltered) | ✅ blocked (`SteamAPI_Init` returns false, game never gets the interface) | **M2** |
| Game → Steam stats / achievements (`SetStat`, `SetAchievement`) | Same as above | ❌ | ✅ blocked | **M2** |
| Steam Cloud save sync | Steam client pushes/pulls `UserData/` on its own schedule | ❌ | ✅ indirectly (init-fail means game never enters cloud-enabled state) | **M2** |
| Save file re-upload on next online launch (queued or embedded submissions) | Uninstall → play vanilla → the game (or Steam) resubmits what's in the save | ❌ | ❌ (proxy's gone after uninstall) | **M3** (modded `UserData/` discarded on uninstall, vanilla restored) |
| Rule silently removed while game is running (VPN resets firewall, group policy, malware, user tinkering) | Game keeps playing but block is gone | ❌ (no trip-wire yet) | ❌ (DLL file is still our proxy, so Steam uploads still blocked, but web-service path is open) | **M4** (in-DLL precondition re-check every ~30 s + live `connect()` probe + trip-wire banner) |

**The takeaway after M2:** every network call the *game process* can initiate
is stopped, either at the firewall (web-service path) or at the DLL boundary
(Steamworks path). The remaining leaks are:
- Anything already **queued inside `Steam.exe`** before safe-base was installed
  — closes when we ship **M3** and the modded save is thrown away on uninstall.
- Anything the game **writes to its own save** that could re-trigger an
  upload later — same fix, M3.
- Silent removal of the firewall rule (or the proxy DLL) **during a session** —
  M4's version.dll trip-wire.

## What M1 + M2 don't do yet

- Detect the firewall rule being removed mid-session — ❌ **no**, M4.
- Detect the proxy DLL being replaced with the real one mid-session — ❌ **no**, M4.
- Move or back up the save — ❌ **no**, M3.
- The `base.psm1` SDK content mods use — ❌ **no**, M5.

Until M5 lands, no content mod is safe to distribute. But M1 + M2 together
mean the *safety story is real* for a user who manually applies a bundle edit
in their own install for personal play.
Use `../overlay/` in the meantime (cosmetic only, does not touch gameplay,
and therefore does not need the safety framework).

## Requirements (for M1 + M2)
- Windows x64, *Leaf it Alone* on Steam (you must own the game).
- Admin PowerShell for the firewall rule (self-elevates via `Start-Process -Verb RunAs`).
- Later milestones add: the game in **borderless / windowed** mode (M4
  overlay), Python 3.12 + UnityPy for content mods (M6).
