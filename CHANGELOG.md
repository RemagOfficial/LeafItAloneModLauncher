# Changelog

All versions support *Leaf it Alone* on Windows x64 (Unity 6000.5.6f1, IL2CPP).

## 1.0.0
Initial release.

- Native `version.dll`-proxy overlay that draws a GDI panel on top of the game, independent
  of the renderer (works without touching IL2CPP, which is protected in this build).
- Panel shows a title, a live local clock, and session uptime.
- **Live in-game process metrics**: working-set RAM and CPU %, read from the running game
  process with safe, read-only OS APIs (updates ~once a second; watch RAM jump when a level loads).
- **F9** show/hide toggle (hotkey configurable).
- Fully config-driven via `LeafOverlay.ini` — `ToggleKey`, `Corner`, `AnchorX/AnchorY`, `Margin`,
  `PanelWidth/Height`, `Alpha`, `ShowTitle/ShowClock/ShowUptime/ShowMetrics`, `Title`, `Subtitle`.
- Reversible `install_overlay.ps1` (auto-detects the Steam install, backs up any existing
  loader) and `uninstall_overlay.ps1`.
- Ships C++ source + a prebuilt `version.dll`. **No game files are included** — you must own
  the game on Steam.
