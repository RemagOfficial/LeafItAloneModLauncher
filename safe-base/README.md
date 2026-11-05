# `safe-base/` — Offline-safety framework

**Status: M1 + M2 + M3 landed and in-game tested (2026-11-05). The three
layers close every upload path the game process can initiate AND keep modded
progression off your real save. M4-M6 (banner overlay + `connect()` probe, PS
SDK, first content mod) are still planned; roadmap at the bottom.**

Install this **before** any mod in `../content-mods/`.

## Quick start (M1 + M2 + M3)

```powershell
# 1. Adds a Windows Firewall OUTBOUND-BLOCK rule for Leaf it Alone.exe.
# 2. Replaces steam_api64.dll with a stub proxy (real one backed up).
# 3. Moves the game's Saves/ folder to a timestamped backup and creates an
#    empty placeholder so modded play writes to a throwaway save.
# Self-elevates via UAC; asks for confirmation ("YES") first. Refuses if the
# game is currently running.
.\safe-base\install.ps1

# Read-only report: firewall rule state, Steam proxy state, save-isolation
# state, backup integrity, manifest, registered mods.
.\safe-base\status.ps1

# Restores Saves/ first (the modded Saves/ is PARKED, never deleted), then
# the real steam_api64.dll, then removes the firewall rule LAST. Refuses if
# the game is running.
.\safe-base\uninstall.ps1
```

Overrides:

- Game path: `install.ps1 -GameExe "D:\some\other\Leaf it Alone.exe"`
- Save path (rare): `install.ps1 -SavePath "..."`
- Skip Steam proxy (M1-only testing): `install.ps1 -SkipSteam`
- Skip save isolation (M1+M2-only testing): `install.ps1 -SkipSaves`
- Fresh install where Saves/ doesn't exist yet: `install.ps1 -AllowMissingSaves`

**Important for M3:** if the game has Steam Cloud enabled (safe-base detects
this by looking for `Saves/steam_autocloud.vdf`), install prints a warning
and tells you to disable it manually:
`Steam Library -> right-click Leaf it Alone -> Properties -> Updates -> uncheck
"Keep game saves in the Steam Cloud"`. safe-base never edits your Steam config.

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

*(Below is the full framework design. **M1 + M2 + M3 = steps 1, 2 and 3.**
The other steps arrive as M4-M5 land; each milestone is independently
testable.)*

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
3. **[M3 landed]** Refuses to run if the game process is currently active
   (never mutate live files). Then locates the game's `Saves/` folder in
   Unity's standard LocalLow path
   (`%USERPROFILE%\AppData\LocalLow\Eternity\Leaf it Alone\Saves`), records a
   file + hash manifest of what was there, and does an atomic same-drive
   rename into `%LOCALAPPDATA%\LeafItAloneSafe\backup\saves-<UTC timestamp>\`.
   Creates a fresh empty `Saves/` with an internal `.safebase-empty` marker
   so status can recognize it. If the folder contained
   `steam_autocloud.vdf`, install flags that Steam Cloud is enabled and
   prints the click-path to disable it in the Steam client (safe-base never
   edits Steam's own config). Any modded play now writes to a throwaway save.
   - Override with `-SkipSaves` if you only want M1+M2. If Saves/ doesn't
     exist yet (never played), pass `-AllowMissingSaves`.
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

1. Refuses if any content mods are still registered (`uninstall-all.ps1`
   reverts them first, then does this; the guard fires from M5 when the
   registry fills up).
2. Refuses if the game process is currently running.
3. **[M3 landed]** Restores the vanilla `Saves/` first. Any modded `Saves/`
   present is **parked** as
   `%LOCALAPPDATA%\LeafItAloneSafe\backup\saves-modded-<ts>\` — never
   deleted, so nothing is lost even if you want to keep exploring a modded
   run later. If the current `Saves/` only holds the empty placeholder (never
   played), it's removed cleanly.
4. **[M2 landed]** Restores the real `steam_api64.dll` (refusing if what's
   at that path now isn't our recorded proxy).
5. Removes `version.dll`, `LeafOverlay.ini`. *(M4)*
6. Removes the firewall rule. **Last** — the network stays blocked until the
   file state is fully vanilla again.
7. Removes the manifest.

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
| `install.ps1` | **M1 + M2 + M3 landed** | Firewall rule + Steam proxy + save isolation + verify + manifest. Self-elevates. Refuses if the game is running. Rolls back earlier steps if a later one fails. New flags: `-SavePath`, `-SkipSaves`, `-AllowMissingSaves`. |
| `uninstall.ps1` | **M1 + M2 + M3 landed** | Refuses if the game is running. Restores Saves/ first (modded parked, never deleted), then the real `steam_api64.dll`, then removes the firewall rule LAST. Refuses if content mods are still applied (guard is live; registry fills up in M5). |
| `status.ps1` | **M1 + M2 + M3 landed** | Read-only: firewall rule + service + Steam-proxy class (proxy / real / absent / unknown) + save-isolation state (isolated / active / missing / n/a) + backup integrity + Steam Cloud rehydration warning + manifest + registered mods. No admin. |
| `common.ps1` | shared | Dot-sourced helpers. M1: `Find-GameExe`, `Test-IsAdmin`, `Get-FirewallState`. M2: `Find-GameSteamDll`, `Install-SteamProxy`, `Restore-SteamProxy`, `Classify-SteamDll`, `Get-FileSha256`, `Get-GameFolderFromExe`. M3: `Find-GameSaveFolder`, `Get-SaveFolderStats`, `Test-SteamCloudMarker`, `Install-SaveIsolation`, `Restore-SaveIsolation`. Constants: rule name/display/description, state dir, manifest path, backup dir, `SteamProxySource`, `SaveRelSegments`, `SteamCloudMarker`. |
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
- **M2 - LANDED, TESTED** — `steam_api64.dll` stub proxy. `install.ps1` finds the
  game's real `steam_api64.dll`, moves it to `%LOCALAPPDATA%\LeafItAloneSafe\
  backup\steam_api64.dll`, drops the prebuilt proxy at the same path, and
  verifies with hash match + size classification. `uninstall.ps1` restores
  the real DLL first (refusing if what's there now isn't our proxy — so we
  never clobber a manual restore). `status.ps1` shows the current DLL class.
  Verified end-to-end in-game 2026-11-05: game launches normally with the
  proxy in place (no "Steam required" error, no crash — the game degrades
  gracefully when `SteamAPI_InitFlat` returns false, so no compat-mode
  variant is needed); the in-game leaderboard now shows empty rather than
  populating from `Steam.exe`. Achievements / stats / Steam Cloud sync paths
  are structurally closed the same way. Test plan:
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
- **M3 - LANDED, TESTED** (2026-11-05, in-game) — Save isolation. `install.ps1`
  refuses if the game is running, records the pre-move Saves/ file list (per
  file: rel path, size, SHA-256) into the manifest, does an atomic
  same-drive rename into `%LOCALAPPDATA%\LeafItAloneSafe\backup\saves-<ts>\`,
  and creates an empty placeholder `Saves/` with a `.safebase-empty` marker.
  Detects Steam Cloud via `steam_autocloud.vdf`; if present, install prints
  the click-path to disable it manually (safe-base never edits Steam's
  config). `uninstall.ps1` **always parks** the modded Saves/ at
  `backup\saves-modded-<ts>\` (never deletes it) before restoring the
  original. `status.ps1` reports isolation state and warns if a
  `steam_autocloud.vdf` reappears at the live path (a rehydration signal).
  Manifest `schemaVersion` 2 -> 3 with a new `saveIsolation` block. Rollback
  works in sequence: if M3 fails after M1 + M2 succeeded, install unwinds
  Saves/, then the DLL, then the firewall rule. Test plan:
  1. Close the game.
  2. *(Optional but recommended)* disable Steam Cloud for Leaf it Alone in
     the Steam client first.
  3. `install.ps1` -> confirm plan -> should print `[M3] backup: ...` and
     `[M3] pre-move content: N file(s), K bytes`. If Steam Cloud was
     detected, install also prints the disable-cloud reminder.
  4. `status.ps1` -> `[M3] current state: ISOLATED (empty placeholder
     present)`, `backup present: yes`, verdict `M1 + M2 + M3 active`.
  5. Launch the game. It should start at the main menu as if first-run
     (no saved game slots). Play a short bit, save a game, quit.
  6. `status.ps1` -> `[M3] current state: ACTIVE (N file(s) in Saves/) -
     game has been played while modded`. Verify the modded save exists at
     the game's normal path.
  7. `uninstall.ps1` -> refuses if game running; else restores Saves/ first
     (modded folder parked at `saves-modded-<ts>`), then restores the real
     DLL, then removes the firewall rule last.
  8. Manually inspect: your pre-install Saves/ is back at the game's
     normal path; the modded Saves/ is at
     `%LOCALAPPDATA%\LeafItAloneSafe\backup\saves-modded-<ts>\`. Launch the
     game one more time - your vanilla progression should be exactly what
     you had before all of this.
  9. `status.ps1` -> "not installed".
- **M4 (planned)** — Persistent banner + startup preconditions, merged into
  the existing `../overlay/version.dll` (one DLL, HUD and safety share a
  binary; `LeafOverlay.ini` decides what draws). When `[safebase] Enabled` is
  true the overlay draws a non-hideable banner ("OFFLINE · MODDED PLAY ·
  SCORES WILL NOT BE SUBMITTED") and runs a **one-shot** startup check from
  inside the game process: file-size match on the Steam proxy, presence of
  the `Saves/` isolation marker, and a live `connect(1.1.1.1:443)` probe (a
  program-scoped firewall rule can only be measured from the exe it scopes
  to, so the probe is genuinely informative in-DLL and not from `status.ps1`).
  If any check fails the banner turns red and the details go to
  `LeafOverlay.log`. We do **not** terminate the game or block play: a user
  who manually breaks a precondition is responsible for their own choices;
  safe-base only surfaces what its install did. `install.ps1` drops
  `version.dll` + `LeafOverlay.ini` (backing up any existing overlay install
  first, same pattern as `steam_api64.dll`).
- **M5** — `base.psm1` SDK + registry + `uninstall-all.ps1` + `recheck.ps1`.
- **M6** — First content mod (`bag-capacity`) as the SDK's real test.

## Leak paths — what M1 + M2 + M3 block vs what's left

Leaf it Alone ships both `com.rlabrecque.steamworks.net.dll` (Steamworks) and
`UnityWebRequestModule.dll` (the game's own HTTP client). Two upload paths,
plus save-mediated re-uploads. Three mitigations.

| Leak path | How it works | M1 (firewall) | M2 (proxy) | M3 (save isolation) | What closes it |
|---|---|---|---|---|---|
| Game → its own web service (HTTPS via `UnityWebRequest`) | `Leaf it Alone.exe` opens a direct TCP+TLS connection to a dev-hosted endpoint | ✅ blocked (SYN dies) | n/a | n/a | **M1** |
| Game → Steam leaderboard upload (`UploadLeaderboardScore`) | Game calls `steam_api64.dll` → loopback IPC → `Steam.exe` → HTTPS | ❌ (Steam's exe isn't the rule target; loopback unfiltered) | ✅ blocked, **tested in-game 2026-11-05** (leaderboard renders empty) | n/a | **M2** |
| Game → Steam stats / achievements (`SetStat`, `SetAchievement`) | Same as above | ❌ | ✅ blocked | n/a | **M2** |
| Steam Cloud save sync (game-mediated) | Game enters cloud-enabled state and hands files to Steam client for push | ❌ | ✅ blocked (init fail means game never enters cloud state) | ✅ belt-and-braces (Saves/ moved aside, marker file travels with backup) | **M2 + M3** |
| Steam Cloud rehydration (client-mediated) | Steam client pushes or pulls files at the Saves/ path independently of the game | ❌ | ❌ (proxy doesn't reach into Steam's own sync scheduler) | ⚠️ mitigated (fresh empty Saves/ has no cloud identity, but if Steam has a cached cloud copy it may still sync files back into the placeholder) | **M3 manual cloud-disable prompt** (safe-base does not edit Steam's config; you click it off) |
| Save file re-upload on next online launch (queued or embedded submissions) | Uninstall → play vanilla on the same save → the game (or Steam) resubmits what's in the save | ❌ | ❌ (proxy's gone after uninstall) | ✅ closes it - modded Saves/ is parked outside the game path, so vanilla play after uninstall uses the pre-install save, which never saw the mod | **M3** |
| Rule silently removed while game is running (VPN resets firewall, group policy, malware, user tinkering) | Game keeps playing but block is gone | ❌ (no trip-wire) | ❌ (DLL file is still our proxy, so Steam uploads stay blocked, but the web-service path reopens) | ❌ (Saves/ isolation is file-based and unaffected by rule state, but new modded writes still go into the isolated Saves/ - fine) | **Accepted risk.** safe-base does not police the user's own machine after install. The M4 banner runs a one-shot outbound probe at startup and reflects the state install left behind. If you turn the rule off mid-session, that's your call. Reinstall (or `status.ps1`) re-verifies. |

**The takeaway after M3:** every network call the *game process* can initiate
is stopped (M1 + M2), and modded progression is written to a throwaway save
that never touches your real one (M3). The remaining leaks are:
- Steam client's **own** cloud rehydration into the isolated `Saves/` (rare,
  only if you don't disable Steam Cloud when prompted) — M3's install-time
  warning + `status.ps1` rehydration check.
- Firewall rule or proxy DLL tampering **during a session** — accepted risk.
  M4's startup banner verifies state once when the game starts and shows
  red/green; we don't police manual changes mid-play.

## What M1 + M2 + M3 don't do yet

- Show the user at a glance that they're playing safely — ❌ **no**, M4
  (persistent banner drawn from inside the game process).
- Prove outbound is blocked from the game's own process (a PowerShell
  `Test-NetConnection` tests PowerShell, not the game) — ❌ **no**, M4
  (live `connect(1.1.1.1:443)` at startup).
- Programmatically disable Steam Cloud (edits `localconfig.vdf`) — deferred
  behind a manual prompt for now; revisit if the rehydration case shows up.
- The `base.psm1` SDK content mods use — ❌ **no**, M5.

Until M5 lands, no content mod is safe to distribute. But M1 + M2 + M3
together mean the *safety story is real* for a user who manually applies a
bundle edit in their own install for personal play. Use `../overlay/` in the
meantime (cosmetic only, does not touch gameplay, and therefore does not need
the safety framework).

## Requirements (for M1 + M2 + M3)
- Windows x64, *Leaf it Alone* on Steam (you must own the game).
- Admin PowerShell for the firewall rule (self-elevates via `Start-Process -Verb RunAs`).
- Game process closed on install and uninstall (safe-base refuses if
  `Leaf it Alone` is running).
- **For M3 with Steam Cloud enabled:** disable it manually for the game
  before playing modded. safe-base will warn you at install time if the
  marker is present.
- Later milestones add: the game in **borderless / windowed** mode (M4
  overlay), Python 3.12 + UnityPy for content mods (M6).
