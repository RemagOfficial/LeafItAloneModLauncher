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
- First content mod planned: `content-mods/bag-capacity/` (M6).

## 1.0.0
Initial release: the `overlay/` example.
- Native `version.dll`-proxy overlay for the protected Unity IL2CPP build.
- On-screen panel with title, live clock, session uptime, and **real live
  process metrics** (working-set RAM + CPU %, read from the running game).
- F9 show/hide toggle, all configurable via `LeafOverlay.ini`.
- Reversible `install_overlay.ps1` / `uninstall_overlay.ps1`.
- Ships source + prebuilt DLL, C++ one-file template, 21 s showcase clip.
- No game files included.
