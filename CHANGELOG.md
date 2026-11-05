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
