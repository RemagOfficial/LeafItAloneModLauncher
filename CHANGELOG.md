# Changelog

Notable changes across this monorepo. Each sub-project also has its own
release notes inside its README.

## Unreleased
- Restructured into a monorepo: `overlay/`, `safe-base/`, `content-mods/`.
- `safe-base/` design agreed (offline framework: verified firewall block,
  Steam proxy, save isolation, precondition trip-wire, PS SDK). Implementation
  roadmap M1-M6 in `safe-base/README.md`.
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
