# `safe-base/` — Offline-safety framework

**Status: M1 + M2 + M3 + M4 landed and in-game tested (2026-11-05); M5 (the
`base.psm1` SDK + content-mod registry + `uninstall-all.ps1`) landed and
smoke-tested. M4 adds
the persistent OFFLINE banner and a one-shot in-game startup probe (the
outbound `connect(1.1.1.1:443)` test only makes sense from inside the
game's own process). In safe-base mode the overlay is **minimal**: no HUD
panel at all, just the banner as plain outlined text so it doesn't distract
during play. Together the four layers close every
upload path the game process can initiate AND keep modded progression off
your real save AND show you at a glance that the safety is active. M5 makes
that safety reusable: content mods import `base.psm1`, re-verify the stack,
back up every file they touch, and register it so `uninstall-all.ps1` can
reverse all of it. M6 (the first content mod, `bag-capacity`) is still
planned; roadmap at the bottom.**

Install this **before** any mod in `../content-mods/`.

## Quick start (M1 + M2 + M3 + M4)

```powershell
# 1. Adds a Windows Firewall OUTBOUND-BLOCK rule for Leaf it Alone.exe.
# 2. Replaces steam_api64.dll with a stub proxy (real one backed up).
# 3. Moves the game's Saves/ folder to a timestamped backup and creates an
#    empty placeholder so modded play writes to a throwaway save.
# 4. Drops the OFFLINE-banner version.dll overlay (any existing version.dll
#    is backed up, not deleted) with [safebase] Enabled=true. In this mode
#    the overlay is minimal — no HUD panel, just a plain-text persistent
#    banner — and it probes the three preconditions from inside the game
#    process at startup.
# Self-elevates via UAC; asks for confirmation ("YES") first. Refuses if the
# game is currently running.
.\safe-base\install.ps1

# Read-only report: firewall rule state, Steam proxy state, save-isolation
# state, overlay banner slot + SHA + Enabled flag, backup integrity, manifest,
# registered mods.
.\safe-base\status.ps1

# Restores overlay first (any pre-existing version.dll comes back; a foreign
# DLL dropped in after us is parked, never deleted), then Saves/ (modded
# parked), then the real steam_api64.dll, then removes the firewall rule LAST.
# Refuses if the game is running.
.\safe-base\uninstall.ps1
```

Overrides:

- Game path: `install.ps1 -GameExe "D:\some\other\Leaf it Alone.exe"`
- Save path (rare): `install.ps1 -SavePath "..."`
- Skip Steam proxy (M1-only testing): `install.ps1 -SkipSteam`
- Skip save isolation (M1+M2-only testing): `install.ps1 -SkipSaves`
- Skip overlay banner install (M1+M2+M3-only testing, or if you already run
  the standalone overlay and just want the safety layers around it):
  `install.ps1 -SkipOverlay`
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

*(Below is the full framework design. **M1 + M2 + M3 + M4 = steps 1, 2, 3
and 4.** Step 5 (the `base.psm1` SDK + content-mod registry) arrives with
M5; each milestone is independently testable.)*

On install, `install.ps1` (self-elevating):

1. **[M1 landed]** Adds a Windows Firewall outbound-block rule on
   `Leaf it Alone.exe` for all profiles (TCP + UDP), then **verifies** the rule
   exists / is enabled / has the right action / direction / profile, and that
   the Windows Firewall service (`MpsSvc`) is running. The install aborts on
   any verification failure. Writes an install manifest at
   `%LOCALAPPDATA%\LeafItAloneSafe\manifest.json`.
   - The live outbound `connect(1.1.1.1:443)` probe that a PowerShell script
     can't perform (firewall rules only affect the target program's traffic)
     runs **at M4** inside `version.dll`, from the game process itself.
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
4. **[M4 landed]** Merges the OFFLINE banner and a startup preflight probe
   into the same `version.dll` the standalone overlay ships (`../overlay/
   version.dll`). If a `version.dll` and/or `LeafOverlay.ini` already exist
   in the game folder (e.g. from a prior overlay install), they're moved to
   `%LOCALAPPDATA%\LeafItAloneSafe\backup\overlay\`. Our build is copied in
   and a fresh `LeafOverlay.ini` is written with `[safebase] Enabled=true`
   and `ExpectedSteamProxySize=<size M2 just installed>`.
   On first game launch, `version.dll` (running inside `Leaf it Alone.exe`)
   switches to **minimal banner-only mode**: the HUD panel is not drawn at
   all, F9 is ignored, and a **persistent plain-text line**
   `OFFLINE · MODDED PLAY · SCORES WILL NOT BE SUBMITTED` appears at
   top-centre — text with a thin black outline, no box or backdrop, so it
   stays readable without distracting during play. The banner can never be
   hidden. Alongside it, a **one-shot** preflight runs:
   (a) the on-disk `steam_api64.dll` matches the expected proxy size,
   (b) the `.safebase-empty` marker exists at the isolated Saves/ path,
   (c) a non-blocking `connect(1.1.1.1:443)` **times out** (which proves the
       program-scoped firewall rule is enforcing for THIS process).
   If all three pass the text is pale green; any failure turns it red with a
   compact reason. We deliberately do **not** terminate the
   process, show a MessageBox, or add a mid-session trip-wire — a persistent
   visible warning plus a red state is enough. If a user manually removes
   the firewall rule mid-session, that's their call; we don't re-probe
   periodically.
   - Override with `-SkipOverlay` if you want M1-M3 only, or if you already
     run the standalone overlay and want safe-base to leave the slot alone.
5. **[M5 landed]** Deploys the SDK to `%LOCALAPPDATA%\LeafItAloneSafe\base.psm1`
   and records it in `manifest.json` (`schemaVersion` 5, a new `sdk` block).
   Content mods import that file; the content-mod registry itself is a separate
   `mods.json` the SDK creates lazily on the first `Register-Mod`, so applying
   or reverting a mod never rewrites the safety manifest.

On uninstall, `uninstall.ps1`:

1. Refuses if any content mods are still registered in `mods.json`
   (`uninstall-all.ps1` reverts them first, then does this; the guard reads
   the live registry).
2. Refuses if the game process is currently running.
3. **[M4 landed]** Restores the overlay slot FIRST: our `version.dll` is
   deleted and the prior backup (if any) is moved back into place; a fresh
   standalone-overlay install thus comes back before anything else changes.
   If what's at the slot now **isn't** our recorded SHA (e.g. the user built
   their own overlay over ours), the current DLL and INI are **parked** at
   `%LOCALAPPDATA%\LeafItAloneSafe\backup\overlay-version-modified-<ts>.dll`
   — never deleted — and the prior backup is restored.
4. **[M3 landed]** Restores the vanilla `Saves/`. Any modded `Saves/`
   present is **parked** as
   `%LOCALAPPDATA%\LeafItAloneSafe\backup\saves-modded-<ts>\` — never
   deleted, so nothing is lost even if you want to keep exploring a modded
   run later. If the current `Saves/` only holds the empty placeholder (never
   played), it's removed cleanly.
5. **[M2 landed]** Restores the real `steam_api64.dll` (refusing if what's
   at that path now isn't our recorded proxy).
6. **[M1 landed]** Removes the firewall rule. **Last** — the network stays
   blocked until the file state is fully vanilla again.
7. Removes the manifest.

The overlay **will run** even when preconditions fail — it just draws a red
banner with the reason instead of a green one. We deliberately leave that
call with the user: the banner is a loud, in-your-face warning, not a
kill switch. If you see red, safe-base wants you to stop playing and
reinstall.

## SDK content mods use (`base.psm1`)

`install.ps1` deploys a **self-contained** module to
`%LOCALAPPDATA%\LeafItAloneSafe\base.psm1`. It does not depend on the repo, so
a content mod imports exactly one file and inherits the whole safety story:

```powershell
Import-Module "$env:LOCALAPPDATA\LeafItAloneSafe\base.psm1" -DisableNameChecking
```

(`-DisableNameChecking` silences the benign "unapproved verbs" warning for the
readable names like `Backup-Original` / `Revert-Mod`; the verbs are intentional.)

| Function | What it does |
|---|---|
| `Assert-SafeBaseActive` | Throws unless the base is installed (manifest present). Call first. Returns the manifest object. |
| `Assert-SafePreconditions` | Re-verifies the **entire** stack right now — M1 rule present+enforcing, M2 proxy at the recorded SHA, M3 `Saves/` placeholder present, M4 overlay at the recorded SHA — and throws with every failure listed. Call before touching a single game file. `-AllowMissingOverlay` for `-SkipOverlay` installs. |
| `Backup-Original -ModName X -TargetPath <file>` | Snapshot one file the mod is about to overwrite (or add) into `%LOCALAPPDATA%\LeafItAloneSafe\mod-backups\X\`, **before** writing. Returns a record (target, `originalExists`, `originalSha256`, backup path). A path that doesn't exist yet records `originalExists=false`, which `Revert-Mod` turns into a clean delete. Collect these records. |
| `Register-Mod -Name X -Version V -Files @(...)` | Write/replace X's entry in `mods.json` from the `Backup-Original` records (plus `appliedSha256` read back from the just-written file). |
| `Get-SafeBaseModRegistry` | Array of registered mod entries (empty if none). |
| `Restore-ModFiles -Name X` | Put X's files back: restore each pristine original (parking a modified current file, never deleting), and remove files X added (parked too). Already-original files are detected by SHA and skipped — idempotent. |
| `Unregister-Mod -Name X` | Drop X's registry entry without touching files. |
| `Revert-Mod -Name X` | `Restore-ModFiles` + `Unregister-Mod` — the whole revert. |
| `Test-GameRunning` | `$true` if `Leaf it Alone` is running (mods must refuse to mutate live files). |
| `Get-SafeBasePaths` | The SDK's view of its state locations (for logging). |

A mod's `apply.ps1` in full:

```powershell
Import-Module "$env:LOCALAPPDATA\LeafItAloneSafe\base.psm1" -DisableNameChecking
$ErrorActionPreference = 'Stop'
if (Test-GameRunning) { throw "Close the game before applying this mod." }
Assert-SafeBaseActive          # refuses to run outside the safe-base framework
Assert-SafePreconditions       # refuses if any safety layer is broken right now

# 1. back up EVERY file you will touch, before writing anything.
$targets = @($bundlePath, $configPath)
$records = foreach ($t in $targets) { Backup-Original -ModName 'my-mod' -TargetPath $t }

# 2. write the mod's replacement / new files into place...
Copy-Item $myNewBundle $bundlePath -Force

# 3. register so safe-base can reverse it.
Register-Mod -Name 'my-mod' -Version '1.0.0' -Files $records | Out-Null
```

And `revert.ps1` is one line:

```powershell
Import-Module "$env:LOCALAPPDATA\LeafItAloneSafe\base.psm1" -DisableNameChecking
if (Test-GameRunning) { throw "Close the game first." }
Revert-Mod -Name 'my-mod'
```

`uninstall-all.ps1` runs `Revert-Mod` for every registered mod, then `uninstall.ps1`.
`uninstall.ps1` itself **refuses** while any mod is still registered, so the
framework can never be torn out from under applied content.


## Commands

| Command | Status | What it does |
|---|---|---|
| `install.ps1` | **M1 + M2 + M3 + M4 + M5 landed** | Firewall rule + Steam proxy + save isolation + overlay banner slot + SDK deploy + verify + manifest. Self-elevates. Refuses if the game is running. Rolls back earlier steps if a later one fails. Flags: `-SavePath`, `-SkipSaves`, `-SkipOverlay`, `-AllowMissingSaves`. |
| `uninstall.ps1` | **M1 + M2 + M3 + M4 + M5 landed** | Refuses if the game is running. Refuses if any content mod is still registered in `mods.json`. Restores the overlay slot FIRST (any prior version.dll comes back; a foreign DLL is parked, never deleted), then Saves/ (modded parked), then the real `steam_api64.dll`, then removes the firewall rule LAST, then removes the deployed SDK + manifest. |
| `status.ps1` | **M1 + M2 + M3 + M4 + M5 landed** | Read-only: firewall rule + service + Steam-proxy class (proxy / real / absent / unknown) + save-isolation state (isolated / active / missing / n/a) + overlay slot state (present / SHA match / `[safebase] Enabled` / prior backup integrity) + SDK deploy state + content-mod registry (mods with per-file replace/add) + backup integrity + Steam Cloud rehydration warning + manifest. No admin. |
| `uninstall-all.ps1` | **M5 landed** | Revert EVERY registered content mod (`Revert-Mod`: restore originals, park/remove added files, unregister), then run `uninstall.ps1`. Self-elevates once so mod reverts and the base teardown share one admin pass. Refuses if the game is running. |
| `base.psm1` | **M5 landed** | The self-contained SDK content mods import from `%LOCALAPPDATA%\LeafItAloneSafe\base.psm1`. Exports `Assert-SafeBaseActive`, `Assert-SafePreconditions`, `Backup-Original`, `Register-Mod`, `Get-SafeBaseModRegistry`, `Restore-ModFiles`, `Unregister-Mod`, `Revert-Mod`, `Test-GameRunning`, `Get-SafeBasePaths`. Maintains the `mods.json` registry and the `mod-backups\<mod>\` store. Deployed by `install.ps1`; never depends on the repo at runtime. |
| `common.ps1` | shared | Dot-sourced helpers. M1: `Find-GameExe`, `Test-IsAdmin`, `Get-FirewallState`. M2: `Find-GameSteamDll`, `Install-SteamProxy`, `Restore-SteamProxy`, `Classify-SteamDll`, `Get-FileSha256`, `Get-GameFolderFromExe`. M3: `Find-GameSaveFolder`, `Get-SaveFolderStats`, `Test-SteamCloudMarker`, `Install-SaveIsolation`, `Restore-SaveIsolation`. M4: `Find-GameOverlaySlot`, `Format-SafeBaseOverlayIni`, `Install-VersionOverlay`, `Restore-VersionOverlay`. M5: `Install-BaseSdk`, `Restore-BaseSdk`, `Get-ContentModRegistry`. Constants: rule name/display/description, state dir, manifest path, backup dir, `SteamProxySource`, `SaveRelSegments`, `SteamCloudMarker`, `OverlaySourceDll`, `SdkSource`, `SdkDeployedPath`, `ModsRegistryPath`, `ModBackupRoot`, `SafeBaseSaveMarker`. |
| `steam_proxy/generate_proxy.ps1` | **M2 landed** | Regenerates `steam_proxy.cpp` + `steam_proxy.def` from `exports.txt`. Deterministic; only rerun if the game's SDK changes its export set. |
| `steam_proxy/build_steam_proxy.ps1` | **M2 landed** | Compiles `steam_proxy.cpp` → `steam_api64.dll` (162 KB stub proxy). Ships prebuilt so end users don't need MSVC. |
| `steam_proxy/exports.txt` | M2 source of truth | 1089 export names captured from the game's current `steam_api64.dll`. |
| `../overlay/leaf_overlay.cpp` | **M4 landed (shared with overlay)** | The persistent-banner overlay + one-shot startup probe. Same DLL is the standalone HUD overlay and safe-base's in-game reporter; behaviour switches on `[safebase] Enabled=` in `LeafOverlay.ini`. Built by `../overlay/build_overlay.ps1`, prebuilt at `../overlay/version.dll`. |

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
- **M4 - LANDED** (banner in-game tested 2026-11-05; the minimal
  plain-text revision below needs a quick re-test) — Persistent OFFLINE
  banner and a one-shot in-game startup probe, both merged into the same
  `version.dll` the overlay example ships (`../overlay/version.dll`; a
  `[safebase]` INI section controls behaviour). When `[safebase]
  Enabled=true` the overlay runs in **minimal banner-only mode**: the HUD
  panel (title/clock/uptime/metrics) is NOT drawn at all, F9 is ignored,
  and the banner is **plain text with a thin black outline** (no box, no
  backdrop) so it stays readable without distracting during play. On
  install, safe-base backs up any existing
  `version.dll` and `LeafOverlay.ini` in the game folder to
  `%LOCALAPPDATA%\LeafItAloneSafe\overlay\`, drops the current build of
  `version.dll` and writes a fresh `LeafOverlay.ini` with
  `[safebase] Enabled=true` and `ExpectedSteamProxySize=<size M2 just
  installed>`. On first launch the DLL, running inside `Leaf it Alone.exe`,
  draws the persistent top-centre text ("OFFLINE · MODDED PLAY · SCORES
  WILL NOT BE SUBMITTED") — which can never be hidden — and verifies:
  (a) `steam_api64.dll` in the game's Plugins folder matches that size,
  (b) `.safebase-empty` exists at the Saves/ path,
  (c) a live non-blocking `connect(1.1.1.1:443)` with a 1.5 s timeout does
  **not** complete (which is the only meaningful way to prove the
  program-scoped firewall rule for THIS process is enforcing — PowerShell
  cannot test this because a program-scoped rule only affects its own
  process). If all three pass the text is pale green; any failure turns it
  red and shows a compact reason. We deliberately do **not** add a
  trip-wire: if a user manually removes the rule mid-session, that is their
  choice. `uninstall.ps1` restores `version.dll` and `LeafOverlay.ini`
  **first** (before touching Saves/, DLL or rule) so any pre-existing overlay
  install comes back before anything else changes. Manifest `schemaVersion`
  bumped 3 -> 4 with a new `versionOverlay` block (`sourceDllSha256`,
  `expectedProxySize`, prior backups). `status.ps1` reports the overlay
  slot state (present / SHA match / Enabled / prior backup integrity). New
  flag: `-SkipOverlay`. Test plan:
  1. Close the game (safe-base refuses if it's running).
  2. `install.ps1` -> UAC -> confirm -> watch M1 + M2 + M3 + M4 phases.
     `[M4] Installing persistent-banner overlay (version.dll)...` and
     `startup check will expect steam_api64.dll size = N bytes`.
  3. `status.ps1` -> `[M4] Overlay banner layer (version.dll): PRESENT,
     matches installed SHA`, `LeafOverlay.ini: PRESENT with [safebase]
     Enabled=true`, verdict `M1 + M2 + M3 + M4 active`.
  4. Launch the game. Within a couple of seconds a **pale-green plain-text**
     line `OFFLINE · MODDED PLAY · SCORES WILL NOT BE SUBMITTED` should
     appear at top-centre of the screen. There should be NO HUD panel
     anywhere — safe-base mode is banner-only.
  5. Press F9. Nothing happens (the toggle only exists in standalone
     overlay mode; in safe-base mode the banner can never be hidden).
  6. Optional negative test: while the game is running, open `wf.msc`,
     find `Leaf it Alone Safe (block outbound - modding)` and set Enabled
     = false. Restart the game. The banner text should turn **red** and
     show `outbound NOT blocked`. This is expected — the check only
     runs at startup, so you need a fresh launch. Do NOT continue playing
     like this. Restore the rule and re-enable safe-base before playing.
  7. `uninstall.ps1` -> refuses if game running; else restores version.dll +
     INI first, then Saves/ (parking modded), then steam_api64.dll, then
     removes the firewall rule last.
  8. Verify manually: the game folder no longer has a `version.dll` or
     `LeafOverlay.ini` you did not have before install (any pre-existing
     pair was put back by STEP 1 of uninstall).
  9. `status.ps1` -> "not installed".
- **M5 - LANDED, SMOKE-TESTED** (2026-11-05) — the `base.psm1` SDK + the
  `mods.json` content-mod registry + `uninstall-all.ps1`. `install.ps1` now
  deploys the self-contained SDK to `%LOCALAPPDATA%\LeafItAloneSafe\base.psm1`
  and records it in a `schemaVersion`-5 manifest (`sdk` block); `mods.json` is
  kept separate so registering a mod never rewrites the safety manifest. The
  SDK gives content mods `Assert-SafeBaseActive`, `Assert-SafePreconditions`
  (re-verifies all four layers right now), `Backup-Original`, `Register-Mod`,
  `Get-SafeBaseModRegistry`, `Restore-ModFiles`, `Unregister-Mod`, `Revert-Mod`,
  `Test-GameRunning`. `uninstall.ps1`'s content-mod guard now reads the live
  registry; `uninstall-all.ps1` reverts every mod then tears down the base in
  one elevated pass. (`recheck.ps1` was dropped — M4's in-DLL startup probe
  already does the re-verification, and `Assert-SafePreconditions` is the
  out-of-game equivalent.) Verified with an isolated round-trip smoke test in a
  temp sandbox (register → revert restores originals byte-for-byte, added files
  are parked not deleted, revert is idempotent by SHA, unhealthy base makes
  `Assert-SafePreconditions` throw). In-game apply test lands with M6.
- **M6** — First content mod (`bag-capacity`) as the SDK's real test.

## Leak paths — what M1 + M2 + M3 + M4 block vs what's left

Leaf it Alone ships both `com.rlabrecque.steamworks.net.dll` (Steamworks) and
`UnityWebRequestModule.dll` (the game's own HTTP client). Two upload paths,
plus save-mediated re-uploads. Four mitigations, the last of which is a
verifier and reporter rather than another blocker.

| Leak path | How it works | M1 (firewall) | M2 (proxy) | M3 (save isolation) | M4 (banner + startup probe) | What closes it |
|---|---|---|---|---|---|---|
| Game → its own web service (HTTPS via `UnityWebRequest`) | `Leaf it Alone.exe` opens a direct TCP+TLS connection to a dev-hosted endpoint | ✅ blocked (SYN dies) | n/a | n/a | 🔍 outbound probe verifies block at launch | **M1** (M4 proves it) |
| Game → Steam leaderboard upload (`UploadLeaderboardScore`) | Game calls `steam_api64.dll` → loopback IPC → `Steam.exe` → HTTPS | ❌ (Steam's exe isn't the rule target; loopback unfiltered) | ✅ blocked, **tested in-game 2026-11-05** (leaderboard renders empty) | n/a | 🔍 size-check verifies proxy at launch | **M2** (M4 proves it) |
| Game → Steam stats / achievements (`SetStat`, `SetAchievement`) | Same as above | ❌ | ✅ blocked | n/a | 🔍 same as above | **M2** |
| Steam Cloud save sync (game-mediated) | Game enters cloud-enabled state and hands files to Steam client for push | ❌ | ✅ blocked (init fail means game never enters cloud state) | ✅ belt-and-braces (Saves/ moved aside, marker file travels with backup) | 🔍 marker-file check verifies isolation at launch | **M2 + M3** (M4 proves it) |
| Steam Cloud rehydration (client-mediated) | Steam client pushes or pulls files at the Saves/ path independently of the game | ❌ | ❌ (proxy doesn't reach into Steam's own sync scheduler) | ⚠️ mitigated (fresh empty Saves/ has no cloud identity, but if Steam has a cached cloud copy it may still sync files back into the placeholder) | ⚠️ marker check would fail if Steam Cloud overwrote placeholder with a real save between launches | **M3 manual cloud-disable prompt** (safe-base does not edit Steam's config; you click it off) |
| Save file re-upload on next online launch (queued or embedded submissions) | Uninstall → play vanilla on the same save → the game (or Steam) resubmits what's in the save | ❌ | ❌ (proxy's gone after uninstall) | ✅ closes it - modded Saves/ is parked outside the game path, so vanilla play after uninstall uses the pre-install save, which never saw the mod | n/a (only meaningful while modded) | **M3** |
| Rule silently removed while game is running (VPN resets firewall, group policy, malware, user tinkering) | Game keeps playing but block is gone | ❌ (no trip-wire) | ❌ (DLL file is still our proxy, so Steam uploads stay blocked, but the web-service path reopens) | ❌ (Saves/ isolation is file-based and unaffected by rule state, but new modded writes still go into the isolated Saves/ - fine) | ⚠️ **detects at next launch**, not mid-session — the outbound probe only runs once at game start. If you turn the rule off mid-play, no banner change until you relaunch | **Accepted risk** by design. If a user manually unblocks, that's their call. The banner makes the current state visible at every launch. |

**The takeaway after M4:** every network call the *game process* can initiate
is stopped (M1 + M2), modded progression is written to a throwaway save
that never touches your real one (M3), and a persistent in-game banner
proves the state at every launch (M4). The remaining leaks are:
- Steam client's **own** cloud rehydration into the isolated `Saves/` (rare,
  only if you don't disable Steam Cloud when prompted) — M3's install-time
  warning + `status.ps1` rehydration check.
- Firewall rule or proxy DLL tampering **during a session** — accepted risk
  by design. M4's banner is a startup verifier + always-visible reporter,
  not a trip-wire. We don't police manual changes mid-play.

## What M1 + M2 + M3 + M4 don't do yet

- Programmatically disable Steam Cloud (edits `localconfig.vdf`) — deferred
  behind a manual prompt for now; revisit if the rehydration case shows up.
- Re-run the precondition probe periodically or on hotkey — deliberately
  **no** by design. M4 is a startup check only; a persistent visible banner
  reflects the state the check found. If a user changes the rule mid-session,
  the banner is stale until next launch. This is intentional: safe-base does
  not treat the user as an adversary.
- The `base.psm1` SDK content mods use — ✅ **landed in M5**. It is deployed by
  `install.ps1`, keeps its registry in a separate `mods.json`, and drives
  `uninstall-all.ps1`. (This section lists only what is still genuinely absent.)

The framework is now complete enough that a content mod is safe to build and
distribute — M6 (`bag-capacity`) is the first, and will be the SDK's real
end-to-end test. Until M6 lands you can still use `../overlay/` on its own
(cosmetic HUD, does not touch gameplay, so it needs none of the safety
framework).

## Requirements (for M1 + M2 + M3 + M4)
- Windows x64, *Leaf it Alone* on Steam (you must own the game).
- Admin PowerShell for the firewall rule (self-elevates via `Start-Process -Verb RunAs`).
- Game process closed on install and uninstall (safe-base refuses if
  `Leaf it Alone` is running).
- **For M3 with Steam Cloud enabled:** disable it manually for the game
  before playing modded. safe-base will warn you at install time if the
  marker is present.
- **For M4:** the game must run in **borderless / windowed** mode (Unity's
  `FullScreenWindow` displayMode). Exclusive fullscreen will not composite
  the topmost overlay window. The vanilla game ships borderless, so this
  is normally already satisfied.
- Later milestones add: Python 3.12 + UnityPy for content mods (M6).
