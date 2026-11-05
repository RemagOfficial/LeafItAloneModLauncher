# Changelog

Notable changes across this monorepo. Each sub-project also has its own
release notes inside its README.

## Unreleased
- Restructured into a monorepo: `overlay/`, `safe-base/`, `content-mods/`.
- `safe-base/` design agreed (offline framework: verified firewall block,
  Steam proxy, save isolation, precondition trip-wire, PS SDK). Implementation
  roadmap M1-M6 in `safe-base/README.md`.
- **`safe-base/` M1 landed**: `install.ps1` / `uninstall.ps1` / `status.ps1` +
  shared `common.ps1`. Adds a Windows Firewall OUTBOUND-BLOCK rule for
  `Leaf it Alone.exe` (TCP+UDP, all profiles), auto-detects the game path
  via Steam `libraryfolders.vdf` + drive scan, self-elevates via UAC, refuses
  double-install, verifies rule state + `MpsSvc` service running after
  install, and writes a manifest at
  `%LOCALAPPDATA%\LeafItAloneSafe\manifest.json`. Read-only `status.ps1`
  needs no admin. `uninstall.ps1` already refuses to run while content mods
  are registered (that check fires from M5 when the registry fills up).
- **`safe-base/` M1 tested (2026-11-05)**. Install / status / wf.msc / uninstall
  pass. Observed: in-game leaderboard still browsable while M1 is active. That
  is expected — the traffic is served from `Steam.exe`, a different process our
  exe-scoped rule doesn't match, over loopback IPC the firewall doesn't filter.
  Consequence documented in a new "Leak paths" table in `safe-base/README.md`:
  **M1 alone does not stop Steamworks-based score submissions.** M2
  (`steam_api64.dll` proxy) is what actually closes that leak.
- **`safe-base/` M2 landed**: `steam_api64.dll` stub proxy under
  `safe-base/steam_proxy/`. Auto-generated from the game's real DLL exports
  (1089 names captured with `dumpbin /exports` → `exports.txt` →
  `generate_proxy.ps1` → `steam_proxy.{cpp,def}` → MSVC build → `steam_api64.dll`,
  162 KB). Every function returns `0` (`bool=false`, `HSteamPipe=0`, `NULL`);
  `g_pSteamClientGameServer` is a null-pointer data export to match the real
  DLL's data symbol. `install.ps1` finds the game's real DLL, moves it to
  `%LOCALAPPDATA%\LeafItAloneSafe\backup\steam_api64.dll`, drops the proxy in
  place, and verifies the current file classifies as `proxy`. `uninstall.ps1`
  restores the real DLL FIRST and removes the firewall rule LAST. `status.ps1`
  reports the current DLL class (`proxy` / `real` / `absent` / `unknown`) +
  backup integrity. Manifest `schemaVersion` bumped to 2, `steamProxy` block
  filled with both SHAs and sizes. Rollback: if M2 fails after M1 succeeded,
  install removes the firewall rule so state stays coherent. Skip Steam
  entirely with `-SkipSteam` for M1-only testing. **Combined effect: M1 closes
  the game's own HTTP path (UnityWebRequest); M2 closes every Steamworks
  path (leaderboard upload, achievements, stats, cloud saves) because the game
  never obtains an `ISteamUserStats`. Remaining leaks (M3-M4) are pre-existing
  Steam-side queue and mid-session rule tampering.**
- **`safe-base/` M2 tested (2026-11-05)**. Install / status / in-game /
  uninstall pass end-to-end. Game launches normally with the pure-stub proxy
  in place — no "Steam required" prompt, no crash — so **no compat-mode
  variant is needed** (the game's `SteamAPI_Init() == false` branch already
  handles offline correctly). The in-game leaderboard now renders **empty**,
  whereas before M2 it populated from `Steam.exe` via loopback (that was the
  leak M2 was built to close). Both upload paths (M1 web-service, M2
  Steamworks) are now confirmed closed by observation.
- **`safe-base/` M3 landed**: save isolation in `install.ps1` /
  `uninstall.ps1` / `status.ps1` + shared helpers in `common.ps1`. On
  install, safe-base refuses if the game process is currently running,
  records the pre-move Saves/ file list (rel path + size + SHA-256 per
  file) into the manifest, then does an atomic same-drive rename of
  `%USERPROFILE%\AppData\LocalLow\Eternity\Leaf it Alone\Saves\` into
  `%LOCALAPPDATA%\LeafItAloneSafe\backup\saves-<UTC timestamp>\` and
  creates an empty placeholder Saves/ with a `.safebase-empty` marker.
  Steam Cloud is detected by looking for `steam_autocloud.vdf`; if present
  install prints the click-path to disable it manually (safe-base never
  edits Steam's own config). On uninstall the modded Saves/ is **always
  parked** at `backup\saves-modded-<ts>\`, never deleted, then the
  original is moved back. Ordering: Saves/ first, DLL second, firewall
  rule LAST (so the network stays blocked until file state is fully
  vanilla). Manifest `schemaVersion` bumped to 3, `saveIsolation` block
  populated with the pre-move file list + hashes and `steamCloudVdf`
  flag. `status.ps1` reports isolation state (isolated / active / missing
  / n/a) and warns if a `steam_autocloud.vdf` marker has reappeared at
  the live path (a Steam client rehydration signal). Rollback: if M3
  fails after M1 + M2 succeeded, install unwinds Saves/, then the DLL,
  then the firewall rule in order. New flags: `-SavePath`, `-SkipSaves`,
  `-AllowMissingSaves`. Smoke-tested end-to-end against a temp-tree fake
  Saves/ folder (four files including a fake steam_autocloud.vdf): move,
  placeholder + marker write, modded-content write, park, restore all
  round-trip correctly.
- **`safe-base/` M3 tested (2026-11-05)**. Full cycle in-game: close game,
  install, launch (first-run state, no saved slots), play, save, quit,
  uninstall, relaunch vanilla. All three layers restore correctly; original
  Saves/ returns; modded Saves/ parks under
  `%LOCALAPPDATA%\LeafItAloneSafe\backup\saves-modded-<ts>\`. Noted: the
  Steam Cloud line in `status.ps1` read alarming ("marker was present at
  install" in yellow) even when cloud was already off. Reworded to neutral
  color with the warning reserved for the actual rehydration case (a marker
  reappearing in the live isolated Saves/).
- **`safe-base/` M4 landed** (in-game test pending): persistent OFFLINE
  banner + one-shot startup preflight, merged into the standalone overlay's
  `version.dll` (no separate DLL hijack). `overlay/leaf_overlay.cpp` gains a
  `[safebase]` INI section (default `Enabled = false`, so the standalone
  overlay is unchanged) that draws a colour-keyed top-centre strip
  `OFFLINE · MODDED PLAY · SCORES WILL NOT BE SUBMITTED` which F9 does NOT
  hide, and runs three checks at game startup from inside `Leaf it Alone.exe`:
  (a) on-disk `steam_api64.dll` matches the expected proxy size, (b) the
  `.safebase-empty` marker exists at the isolated Saves/ path, (c) a
  non-blocking `connect(1.1.1.1:443)` with a 1.5 s timeout does NOT complete
  (the outbound probe is the only meaningful way to prove the program-scoped
  firewall rule is enforcing, since PowerShell would test its own process,
  not the game's). All pass = green banner; any fail = red banner with a
  compact reason. Deliberately **no** trip-wire, **no** MessageBox, **no**
  termination, **no** periodic re-check — a user who manually removes the
  rule mid-session is not policed. `safe-base/common.ps1` grows four
  helpers (`Find-GameOverlaySlot`, `Format-SafeBaseOverlayIni`,
  `Install-VersionOverlay`, `Restore-VersionOverlay`) and a new
  `OverlaySourceDll` constant. `install.ps1` adds an M4 step after M3:
  backs up any existing `version.dll` + `LeafOverlay.ini` to
  `%LOCALAPPDATA%\LeafItAloneSafe\backup\overlay\`, copies our build in,
  writes a fresh ASCII INI with `[safebase] Enabled=true` and
  `ExpectedSteamProxySize=<size M2 installed>`. Manifest `schemaVersion`
  bumps 3 → 4 with a new `versionOverlay` block (source DLL SHA, expected
  proxy size, prior backup paths). Rollback unwinds overlay FIRST, then
  Saves, then DLL, then rule. `uninstall.ps1` reorders: overlay restore
  STEP 1 (so any prior standalone-overlay install comes back before anything
  else changes), then Saves, then DLL, then firewall LAST. A foreign DLL at
  the slot (not matching our recorded SHA) is **parked** as
  `overlay-version-modified-<ts>.dll`, never deleted. New flag
  `-SkipOverlay`. `status.ps1` gains an M4 section (version.dll present, SHA
  match, INI present with `[safebase] Enabled=true`, expected proxy size,
  prior backup integrity) and verdict `M1 + M2 + M3 + M4 active` in green;
  `M1 + M2 + M3 without M4` shows yellow (banner missing). Prebuilt
  `overlay/version.dll` rebuilt: 188 416 bytes, SHA-256
  `992FD87186DB93EA8F15B933B030C305538CD3705AA7DA884BC1BDB59AF1D68D`
  (prior overlay commit was 179 200 bytes).
- **`safe-base/` M4 tested (2026-11-05) + minimal revision**. In-game test
  passed: green banner at top-centre, preflight correct, uninstall restored
  the slot first. Per user feedback the safe-base mode was then made
  **minimal**: when `[safebase] Enabled=true` the HUD panel (title/clock/
  uptime/metrics) is NOT drawn at all, F9 is a no-op, and the banner is
  **plain pale-green/red outlined text** (no rounded box, no backdrop) so
  it doesn't distract during play. Window alpha is forced to 255 in
  safe-base mode so the text stays crisp. Standalone overlay mode (safebase
  disabled) is unchanged. `overlay/version.dll` rebuilt at the same size
  (188 416 bytes), new SHA-256
  `D3D735154DE13FF709C0158969C89CB1225A801E09D76F4A6303102EEFC708C1`.
  Follow-up: the plain text showed pink/purple halos around glyph edges —
  ClearType antialiasing blends against the magenta color-key, and those
  intermediate pixels aren't the key so they become opaque. The banner now
  uses a dedicated **NONANTIALIASED** Segoe UI -22 font (exact-colour
  pixels, clean with the 8px black outline). HUD panel fonts unchanged.
  Rebuilt: 188 416 bytes, SHA-256
  `8342659AF2385F4E4CF8BBF813B8DF99BE3F5E6FDB8525F2B1C21D32964CD972`.
- **`safe-base/` M5 landed (2026-11-05)** — the `base.psm1` SDK, the
  `mods.json` content-mod registry and `uninstall-all.ps1`. `base.psm1` is a
  **self-contained** module (no repo dependency) that `install.ps1` now deploys
  to `%LOCALAPPDATA%\LeafItAloneSafe\base.psm1`; a content mod imports that one
  file and inherits the whole safety story. Exports:
  `Assert-SafeBaseActive`, `Assert-SafePreconditions` (re-verifies M1 rule +
  M2 proxy SHA + M3 placeholder + M4 overlay SHA in one call and throws with
  every failure listed), `Backup-Original` (snapshot a file *before* a mod
  overwrites/adds it, into `mod-backups\<mod>\`), `Register-Mod`,
  `Get-SafeBaseModRegistry`, `Restore-ModFiles` (park-modified-then-restore,
  never deletes, idempotent by SHA, removes mod-added files), `Unregister-Mod`,
  `Revert-Mod`, `Test-GameRunning`, `Get-SafeBasePaths`. The registry lives in
  a separate `mods.json` so registering a mod never rewrites the safety
  manifest. `install.ps1` bumps the manifest to `schemaVersion` 5 with a new
  `sdk` block and a `[M5]` deploy step (rolls back cleanly, exit code 9).
  `uninstall.ps1`'s content-mod guard now reads the live `mods.json` and it
  removes the deployed SDK with the manifest; `status.ps1` gains an `[M5]`
  registry + SDK section and a verdict note. New `uninstall-all.ps1` reverts
  every registered mod then tears down the base in one elevated pass.
  `recheck.ps1` from the original roadmap was dropped — M4's in-DLL startup
  probe and `Assert-SafePreconditions` already cover it. Verified with an
  isolated round-trip smoke test in a temp sandbox (all 20 checks pass:
  register → revert restores originals byte-for-byte, added files are parked
  not deleted, revert idempotent, unhealthy base makes
  `Assert-SafePreconditions` throw).
- **`content-mods/` M6 landed (2026-11-05)** — the first real content mod,
  `bag-capacity`, doubling the four leaf-bag capacities (50/200/500/800 →
  100/400/1000/1600, `-Scale` configurable). `patch_bag_capacity.py` reads the
  plain UnityFS `coreassets_assets_meta_*.bundle` TypeTrees with UnityPy and
  rewrites the `capacity` int on the four `*BagCapacity` MonoBehaviours; it
  refuses to run unless it finds the exact vanilla values (so it never
  double-scales or patches the wrong build) and re-opens its own output to
  verify before anything is written back. `apply.ps1` runs on the SDK:
  `Assert-SafeBaseActive` + `Assert-SafePreconditions` (never mods an unsafe
  base), stages + self-verifies the patch in a temp dir, `Backup-Original`s the
  pristine bundle, overwrites, re-reads the LIVE bundle to confirm, auto-restores
  on failure, then `Register-Mod`s. `revert.ps1` calls `Revert-Mod` for a
  byte-identical restore. Verified with an isolated round-trip smoke test
  (temp-sandboxed `LOCALAPPDATA`, copy of the real bundle, nothing real touched;
  all 16 checks pass: 2x applied + live-verified, patcher rejects non-vanilla
  input, revert restores byte-for-byte + drains the registry). Requires Python
  3.12 + UnityPy (3.14 segfaults on the native deps). This closes the safe-base
  framework — every upload path is shut, play is offline-only in a quarantined
  save, and content mods are now gated, backed up and reversible.
- **`content-mods/` M6 follow-up — Addressables CRC (found on the in-game test).**
  The first apply hung the game on a black screen: `Player.log` showed
  `CRC Mismatch. Provided cd9380f1, calculated ... Will not load AssetBundle`.
  The game loads bundles through Unity Addressables 2.9, which CRC-verifies each
  bundle against `aa/catalog.bin`, so a repacked bundle is refused. Added
  `patch_catalog.py`: it nulls this bundle's catalog CRC (little-endian, uniquely
  located and confirmed by the adjacent vanilla-size field; `crc = 0` = "skip
  check", file length unchanged so all other offsets stay valid). `apply.ps1`
  now backs up, edits and registers BOTH the bundle and `catalog.bin`, and rolls
  both back on any failure; `revert.ps1` restores both byte-for-byte. Verified by
  an extended round-trip smoke test (9/9, including byte-identical revert of the
  665 KB catalog).
- **`content-mods/` M7 — bag-capacity is now configurable.** New
  `bag-capacity.json` sets sizes either by a `multiplier` (default x2) or as
  explicit per-bag `sizes`. `apply.ps1` resolves targets with precedence
  *explicit `-Small/-Medium/-Big/-Giant` > `-Scale` > config*, and — key for
  iterating — if the mod is already applied it now `Revert-Mod`s to vanilla
  first, then re-patches from the pristine backup, so **edit config → `apply.ps1`
  → relaunch** changes values with no stacking and no manual revert.
  `patch_bag_capacity.py` gained explicit-target args (still refuses
  non-vanilla input, self-verifies, rejects zero/negative). Version bumped to
  1.2.0. Offline smoke test 11/11 (patcher round-trip + guards + all precedence
  paths). `content-mods/README.md` records "externalise the numbers" as a rule
  for future mods.
- **`content-mods/` + `overlay/` M8 — edit bag sizes in-game (F8).** The native
  `version.dll` overlay grows a hidden, key-toggled editor panel for the bag
  config, so you can retune sizes while playing instead of alt-tabbing to a text
  editor. **F8** shows/hides it; `←/→` pick a bag, `↑/↓` change the value
  (`Shift` ×10), `Enter` saves, `Esc` closes. On Save the overlay writes the four
  sizes into the canonical `%LOCALAPPDATA%\LeafItAloneSafe\bag-capacity.json`
  (as `mode: "sizes"`) and launches the new `apply-on-exit.ps1`, which waits for
  the game to close and then runs `apply.ps1` — so in-game edits are live next
  launch. This respects the IL2CPP constraint (encrypted metadata → no managed
  in-process code; the bundle is locked while the game runs), so it is save-and-
  re-patch-on-exit, never live memory hacking. Config is now a single canonical
  live file seeded from the repo template on first `apply.ps1`; that seed (and a
  top-up on older configs) writes an absolute `_applyHelper` path into the JSON so
  the overlay can find the helper even when the repo and the game are on
  **different drives** — every overlay path is now built absolute from
  `%LOCALAPPDATA%`, not DLL-relative. `[bageditor]` added to `LeafOverlay.ini`
  (`ToggleKey`/`Min`/`Max`/`Step`/`StepBig`). Rebuilt `version.dll` (MSVC, clean);
  offline JSON round-trip (seed + overlay-saved config) verified.
- **`safe-base/` M8 mod-support — `redeploy-overlay.ps1`.** `install.ps1` refuses
  a double install, so iterating on the overlay used to mean a full
  uninstall/reinstall (churning the firewall, proxy and saves) just to swap the
  one `version.dll` in the game folder. This adds a sanctioned one-command refresh:
  re-drops the built `version.dll`, regenerates `LeafOverlay.ini` (so new sections
  like `[bageditor]` land on already-installed games), and updates the manifest
  SHA — touching no firewall/proxy/save state, and refusing while the game runs.
- **`launcher/` L1 — WPF scaffold + data-driven mod manifests.** Begins the
  in-app launcher that replaces the clunky per-mod script UX and the M8 in-game
  F8 editor (retired at L6). A .NET 8 WPF app (`launcher/`) that is an
  **orchestrator, not a rewrite**: every engine call funnels through one
  `ProcessRunner` (`powershell -File`, args quoted via `ArgumentList`, streamed
  stdout/stderr into an in-app Output log drawer, timeout + exit code), so no
  patch/firewall/proxy logic is duplicated in C#. Mods become data-driven via a
  per-mod `content-mods/<id>/mod.json` manifest (id/name/version/order, a
  schema of `config.fields` that will auto-generate editors in L2, `apply`/`revert`
  command templates, `requires`). `bag-capacity` refactored to ship one; the
  launcher lists discovered mods read-only. `global.json` pins the .NET 8 SDK so it
  builds offline against the installed 8.0 packs. L1 headless checks
  (`launcher/tests/HeadlessChecks`) verify discovery, the manifest round-trip,
  `%LOCALAPPDATA%` path expansion and the subprocess runner. Framework-dependent
  publish green; `--self-contained` publish needs the runtime packs (network) —
  documented in `launcher/README.md`.
- **launcher L1 fix** — dropped `InvariantGlobalization` from the csproj: WPF
  bindings call `XmlLanguage.GetSpecificCulture()` and throw "Cannot find
  non-neutral culture related to 'en-us'" without ICU data, so the app started but
  showed no window (a silent, no-obvious-error crash).
- **`launcher/` L2 — schema-driven config editor + live config read/write.**
  `Services/ConfigStore.cs` loads/writes a mod's live `%LOCALAPPDATA%\…\<id>.json`
  via `JsonNode`, addressing values by dotted path (`sizes.small`) and **preserving
  every key the manifest does not describe** (`_comment`, `_applyHelper`, …) so the
  round-tripped file still parses through PowerShell `ConvertFrom-Json` and keeps
  working with `apply.ps1`. `Services/LauncherState.cs` tracks enable + the config
  SHA at last apply in `launcher/state.json`; "pending" = live SHA != applied SHA.
  `ConfigFieldEditor`/`ConfigEditorViewModel` render one control per field by
  declared `type` (slider+box int/float, checkbox bool, combo enum, text box) with
  `when`-gated visibility (bag sizes show only in `mode: sizes`), and the Mods list
  shows a live pending badge. Mods is the interim default tab (L5 restores the
  Dashboard landing). All 32 headless checks pass (L1 discovery + L2 round-trip,
  `when` re-gating, pending badge, `ConvertFrom-Json` acceptance).
  - bag-capacity field ranges tightened (user feedback): size sliders capped
    **0..9999** and multiplier **max 10x** — values above 4 digits overrun the
    in-game bag UI element, so the launcher now clamps them out of range.
- **`launcher/` L3 — apply/revert orchestration.** The launcher now drives the real
  engines instead of reimplementing them. `Services/ApplyOrchestrator.cs` turns a
  manifest's `apply`/`revert` blocks into a `powershell -File …` call through the one
  `ProcessRunner`, substituting `-Python` from `Services/PythonLocator.cs` (bundled
  `runtime/py` → `LEAF_PYTHON` → `C:\Python312`) — to **apply only**, since
  `revert.ps1` declares no `-Python`. It mirrors the scripts' cheap preconditions for
  an instant message (game running → `apply.ps1` exit 3; safe-base missing; Python
  missing → exit 5) while the scripts stay authoritative and roll the bundle + catalog
  back on any failure. `MainViewModel` gains **Apply / Revert** buttons: Apply saves
  pending edits, runs `apply.ps1`, and on a clean exit records `lastAppliedConfigSha` +
  `appliedVersion` + `enabled` in `state.json` so the badge clears; a non-zero exit is
  reported as "game left untouched". Pending is now **enabled-aware** (changed config
  while enabled, *or* an applied mod the user switched off — a revert is owed). Elevation
  is not needed for apply/revert (Steam library is user-writable; only HKCU + the game
  folder are written). 50 headless checks pass — L3 adds the pending lifecycle, the
  `-Yes`/`-Python` arg contract, and a **real subprocess round trip against fake scripts
  in a temp dir** (never the game): launch, streamed output, exit 0 vs exit 7, and a
  revert that proves no undeclared `-Python` is passed.
  - output log now **auto-scrolls** to the newest line (user feedback: it didn't follow
    streaming output). `Views/LogAutoScroll.cs` is a declarative attached behaviour on
    the log `ListBox`; it follows only when you're already near the bottom, so scrolling
    up to read history isn't yanked back down. Kept out of code-behind.
- **`launcher/` L4 — Apply &amp; Launch.** The Dashboard's one action reconciles every mod
  against its enable toggle in manifest `order` (lowest first): an enabled mod whose
  config differs from what is applied is applied, a disabled mod that is still applied is
  reverted, anything in sync is skipped — then the game starts. `ApplyOrchestrator.Plan()`
  holds that decision (pure, unit-tested across the state matrix); `Services/GameLauncher.cs`
  starts the exe safe-base recorded in `manifest.json` (`gameExe`) only if the path still
  exists, and refuses to double-launch when the game is already running. **Any blocked or
  failing mod aborts the sequence before launch**, so the game never boots half-modded;
  with nothing pending the button just launches. The per-mod Apply/Revert logic was refactored
  into shared `ApplyOneAsync`/`RevertOneAsync` cores the bulk loop reuses. 61 headless checks
  pass — L4 adds the plan matrix and manifest-parsing edge cases (missing/blank/moved/malformed
  `gameExe`), with no real game touched by the suite.
- **`launcher/` L5 — safe-base lifecycle + first-run landing.** The Dashboard is now the
  default tab and shows the framework's install state straight from `manifest.json`
  (`installedAt`/`milestone`/`gameExe`) via `SafeBaseService.ReadFrom`, then drives the
  lifecycle as subprocesses only: **Install safe-base** (`install.ps1 -Yes`), **Uninstall all**
  (`uninstall-all.ps1 -Yes`, reverts every registered mod then removes the base), and **Run full
  status** (`status.ps1`, read-only). The launcher never reimplements elevation or teardown —
  `install.ps1`/`uninstall-all.ps1` self-elevate and the non-elevated parent `WaitForExit()`s on
  the elevated child and re-exits its code, so the parent exit code we read is the real outcome
  (we pass `-Yes` so nothing blocks on an interactive `Read-Host`). A first-run call-to-action
  appears when the base is absent; the full status report streams to the log and its trailing
  `VERDICT:` line is surfaced on the panel (`SafeBaseService.ExtractVerdict`). Install/Uninstall
  pre-check the game-closed and installed/not conditions for a friendly message (the scripts stay
  authoritative) and the safe-base is refreshed after every op; uninstall clears each mod's
  applied marker so badges stay honest. 70 headless checks pass — L5 adds the manifest-parse
  matrix, verdict extraction, and a **real `status.ps1` run against a fake script in a temp dir**
  that also proves `Write-Host` output reaches the redirected stdout. No game or real safe-base
  touched by the suite.
- **`launcher/` polish — coloured Output log.** The scripts' redirected stdout arrives colourless
  (PowerShell drops `Write-Host` colour without a console), so the log was all-white. `Services/LogColor.cs`
  recovers the severity from each line's own markers (a pure, unit-tested classifier — failures red,
  successes green, warnings amber, everything else neutral, matched in priority order so a failing run
  never reads green); `Views/LogLineBrushConverter.cs` maps it to a brush on the log's item template.
  The log stays a flat list of strings — nothing else changes. 83 headless checks pass.
- **`overlay/` + `safe-base/` + `content-mods/` L6 — retire the in-game bag editor.** The
  launcher (L2) now owns bag-size config, so the M8 F8 overlay panel is dead weight. Removed
  from `leaf_overlay.cpp`: the editor globals, the `[bageditor]` ini parse, every `Be*` helper
  (`BeCfgPath`/`BeFindInt`/`BeFindString`/`BeClamp`/`BeLoadConfig`/`BeSaveConfig`/`BeSpawnHelper`)
  and `PaintBagEditor`, plus the `WM_TIMER` key-poll and the `PaintOverlay` draw — the safe-base
  **banner and one-shot preflight are untouched**. Dropped `[bageditor]` from `common.ps1`
  `Format-SafeBaseOverlayIni` and `overlay/LeafOverlay.ini`; deleted the orphaned
  `content-mods/bag-capacity/apply-on-exit.ps1` and the `_applyHelper` path injection in
  `apply.ps1` (nothing launches the waiter any more). Rebuilt `version.dll` (MSVC, clean).

## 1.0.0
Initial release: the `overlay/` example.
- Native `version.dll`-proxy overlay for the protected Unity IL2CPP build.
- On-screen panel with title, live clock, session uptime, and **real live
  process metrics** (working-set RAM + CPU %, read from the running game).
- F9 show/hide toggle, all configurable via `LeafOverlay.ini`.
- Reversible `install_overlay.ps1` / `uninstall_overlay.ps1`.
- Ships source + prebuilt DLL, C++ one-file template, 21 s showcase clip.
- No game files included.
