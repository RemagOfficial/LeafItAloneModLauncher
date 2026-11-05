# Leaf it Alone Mod Launcher

A single GUI app that manages Leaf it Alone mods and the safe-base so users never
have to run the per-mod PowerShell scripts or edit JSON by hand.

**The launcher is an orchestrator, not a rewrite.** It drives the already-tested
engines as hidden subprocesses — `safe-base/install.ps1`, `uninstall.ps1`,
`uninstall-all.ps1`, `status.ps1`, and each mod's `apply.ps1`/`revert.ps1` (which
run the UnityPy patchers). No firewall / Steam-proxy / save-isolation / patch logic
is duplicated in C#, so the safety guarantees built in milestones M1–M7 hold
unchanged.

> This is the primary path going forward. The manual scripts still work and are
> documented in `safe-base/README.md` as the advanced / manual appendix.

## Status

Milestones **L1–L7** and **L9** are complete; **L8** (distribution polish) is pending.

> **L9** is the visual overhaul + functional settings: a live Light/Dark theme with an
> accent picker, a left nav rail (Dashboard / Mods / Settings), and a Settings page that
> actually works — theme/accent applied live and persisted, plus content-root and
> game-exe path overrides. All hand-built WPF styles (no theme packages, so the offline
> build constraint holds). See the "Look & settings (L9)" section below.

**L1** — WPF app that builds and runs; the subprocess `ProcessRunner` (PowerShell
`-File`, `ArgumentList` quoting, streamed Output log, timeout + exit code); mod
discovery from `content-mods/<id>/mod.json`; the manifest schema; `bag-capacity`
refactored to ship one; a read-only mod list.

**L2** — the **schema-driven editor**: selecting a mod renders one control per
manifest field by declared `type` (slider+box for int/float, checkbox for bool,
combo for enum, text box for text), with `when`-gated visibility (e.g. the bag
sizes only show in `mode: sizes`). For numeric fields the **slider and the text
box are decoupled**: the slider stays bound to the manifest's safe `min`/`max`
band, while the box accepts any value except 0 (it rejects blank/non-numeric/zero
and snaps back), so users can push a value past the slider's range on purpose — the
slider then just pins to the nearest bound and the saved JSON keeps the real number.
Edits write the live
`%LOCALAPPDATA%\LeafItAloneSafe\<id>.json`, **preserving every key the schema does
not mention** (`_comment`, `_applyHelper`, …). The enable toggle and applied-config
SHA persist to `launcher/state.json`.

**L3** — **apply/revert orchestration**: the Mods detail pane has **Apply / Revert**
buttons that drive the real `apply.ps1` / `revert.ps1` through `ApplyOrchestrator` +
`ProcessRunner` (never reimplementing patching). `-Python` is resolved by
`PythonLocator` (bundled → `LEAF_PYTHON` → system) and passed to apply only. Cheap
preconditions (game running / safe-base / Python) are mirrored for a friendly early
message while the scripts stay authoritative. On a clean exit the applied SHA +
version are recorded and the badge clears. The Output log **auto-scrolls** to new
lines (following only when you're already at the bottom).

**L4** — **Apply & Launch**: the Dashboard action reconciles every mod against its
enable toggle in manifest `order` (apply what should be live, revert what was switched
off, skip what's in sync), then starts the game from the path safe-base recorded in
`manifest.json` (`GameLauncher`). Any blocked/failed mod stops the sequence **before**
launch, so the game never boots half-modded.

**L5** — **safe-base lifecycle + first-run landing**: the Dashboard (now the default
tab) shows the framework's state from `manifest.json` and drives its install/teardown
through `SafeBaseService` → the real `install.ps1` / `uninstall-all.ps1` / `status.ps1`
(subprocess only; self-elevation is handled by the scripts, whose exit code we read).
**Install safe-base** appears when it is missing (first-run guidance), **Uninstall all**
when present, and **Run full status** streams the full report and surfaces its
`VERDICT:` line. Uninstall also clears every mod's applied marker so the badges stay
honest.

**L6** — **overlay slimdown.** The in-game F8 bag editor is retired now that the
launcher owns bag config: `leaf_overlay.cpp` keeps only the banner + one-shot
preflight, the `[bageditor]` ini keys, `apply-on-exit.ps1` and the `_applyHelper`
injection are gone, and `version.dll` is rebuilt. (Lives in `overlay/` +
`safe-base/` + `content-mods/`, not the C# app.)

**L7** — **mod-runtime detection + the second data-only mod.** Instead of bundling a
private Python (no network at build time, and the launcher runs via `dotnet`), the
launcher *detects* the engine runtime: `Services/PythonHealth.cs` probes the resolved
interpreter once (version + whether `UnityPy` imports) with a pure, unit-tested
classifier, the Dashboard shows a **"Mod engine runtime"** status panel, and when no
usable Python 3.12 + UnityPy is found it opens a guided **setup popup** — the pinned
download link, the exact `pip install UnityPy` command and a **Re-check** that re-probes
without a restart. `ApplyOrchestrator` gates a Python-requiring apply on that probe.
Part B adds **`more-leaves`** — a second UnityPy mod shipped purely as a `mod.json` +
scripts (no launcher code) that densifies each area's `leavesCount`, proving discovery is
fully manifest-driven. 100 headless checks pass.

**L9** — **visual overhaul + functional settings.** The old per-mod scripts and stub
Settings tab are gone from the UX: the window is now a **left nav rail** (brand mark,
Dashboard / Mods / Settings, busy + pending indicators) over a header band, a page
region, a collapsible dark **console** with a Clear affordance, and a slim status bar.
Each page is its own `UserControl` (`Views/DashboardView`, `Views/ModsView`,
`Views/SettingsView`); the schema-driven Mods editor moved into `ModsView` verbatim, so
its behaviour is unchanged. Theming is entirely hand-written — `Theme/Light.xaml` and
`Theme/Dark.xaml` define one shared set of **semantic brushes**, `Theme/Controls.xaml`
supplies every control `Style`/`ControlTemplate` (rounded buttons, restyled slider /
textbox / checkbox / combo / list / scrollbar, the nav item style) and references those
brushes through `DynamicResource`. `ThemeManager` swaps the merged dictionary and
re-derives the accent at runtime, so **Light/Dark and the accent (presets + custom hex)
change live with no restart**; the console stays dark in both themes so log lines stay
legible. `Services/AppConfig.cs` persists `theme`/`accent`/`contentRootOverride`/
`gameExeOverride` to `launcher/settings.json` beside `state.json` (fail-safe load,
normalising bad values). The Settings page wires that up: theme + accent pickers apply
and save on click, a **content-root override** re-scan (`Rescan()` → `TryLocate(start)`)
and a **game-exe override** feed the existing launch seam (`GameLauncher.Launch(exeOverride)`
— override wins only if the file exists, else the safe-base manifest path). Path pickers
use the in-box .NET 8 `OpenFolderDialog` / `OpenFileDialog` and are raised from the VM as
events, opened in the code-behind (dialogs stay out of the VM, as with the Python popup).
Five L9 headless checks cover the `AppConfig` round-trip, override-aware discovery,
game-exe override resolution, the accent lighten/darken/on-accent math and Light↔Dark
token parity; the full suite passes.

Still ahead: **distribution polish** (L8 — README screenshots, honest AI/UnityPy/loader
credits, a GitHub Releases zip, `um publish check`). Publishing stays the user's call.


## Layout

```
launcher/
  LeafItAloneLauncher.sln      one solution, one project
  global.json                  pins the .NET 8 SDK so it builds offline against the
                               locally installed 8.0 targeting packs
  src/Launcher/                the WPF app
    App.xaml / MainWindow.xaml
    ThemeManager.cs            live theme/accent swap (merged-dictionary + derived brushes)
    Theme/                     Light.xaml, Dark.xaml (semantic brushes) + Controls.xaml (styles)
    Views/                     DashboardView / ModsView / SettingsView (UserControls),
                               PythonSetupWindow, converters
    Models/                    ModManifest (+ ConfigField), ModInfo
    Services/                  RepoLocator, ProcessRunner, ModDiscovery, EnvPaths,
                               GameLauncher, SafeBaseService, PythonHealth, AppConfig
    ViewModels/                MainViewModel + ObservableObject + RelayCommand + Section
  tests/HeadlessChecks/        a console harness (no game, no UI) for L1–L9 checks
```

## Build & run (dev)

Requires the **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`, or via
Visual Studio 2022 17.9+). `global.json` pins the launcher to that SDK.

```powershell
cd launcher
dotnet build LeafItAloneLauncher.sln -c Debug        # compiles the app
dotnet run --project src/Launcher                   # launches the WPF window
```

The app locates the content root by walking up from the executable until it finds a
folder with both `content-mods/` and (`safe-base/` for the repo, or `tools/` for a
published layout). From a dev build that resolves to the repo root, so the Mods tab
shows `bag-capacity` straight from `content-mods/bag-capacity/mod.json`.

### Headless checks (fast, no window)

```powershell
cd launcher
dotnet run --project tests/HeadlessChecks
```

Asserts discovery, the manifest round-trip, `%LOCALAPPDATA%` path expansion and the
subprocess runner (exit code + streamed output). Exits non-zero on any failure, so
it can gate the build.

## Look & settings (L9)

The UI is a **left nav rail** with three pages — **Dashboard** (Play first, then safe-base,
mod engine runtime, tools), **Mods** (the schema-driven editor list + detail) and
**Settings**. A collapsible dark console shows the streamed engine output; a status bar
sits at the bottom.

**Settings is functional:**

- **Theme** — Light / Dark / System, plus an **accent** picker (green, teal, blue,
  violet presets or a custom `#RRGGBB`). Every change applies **live** (no restart) and
  persists to `launcher/settings.json`. `System` follows the OS app theme.
- **Content root override** — point the launcher at a different `content-mods/` tree.
  Browse (folder picker) or clear back to auto-detect; on change it re-scans and the
  Mods page follows the new root. The resolved path is shown read-only.
- **Game exe override** — launch a specific executable instead of the path safe-base
  recorded. Browse (`*.exe`) or clear; an override only wins if the file exists,
  otherwise it falls back to the manifest path.
- **Reset to defaults** restores theme, accent and both overrides.

Look is 100% hand-written WPF (no theme packages). If you add a control style, reference
the semantic brushes by `DynamicResource` (never a hard-coded colour) so it re-themes on
a swap; keep the console dark in both palettes.

## Publish (self-contained, for users)

Ships as one folder/zip the user can run with **no .NET install**:

```powershell
cd launcher
dotnet publish src/Launcher/Launcher.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

> **Offline caveat:** `--self-contained` needs the `Microsoft.*(App.)Runtime.win-x64`
> and `Microsoft.NET.ILLink.Tasks` NuGet packages, which are downloaded on first
> publish. On a machine **with internet** this just works; a build environment
> without those packs (or network) can only do the framework-dependent publish
> (`dotnet publish -c Release`), which is what CI here validates. The published
> `publish/` folder then needs the `content-mods/` and engine scripts beside it (see
> the packaging milestone, L7).

## `mod.json` manifest schema

A mod is fully described by `content-mods/<id>/mod.json`. The launcher builds its
mod list and config editors purely from these files — **adding a mod never means
writing app UI code.** Top-level keys:

| Key          | Meaning |
|--------------|---------|
| `id`         | stable folder id (defaults to the folder name) |
| `name`       | display name |
| `version`    | mod version |
| `description`| shown in the Mods detail pane |
| `homepage`   | optional link / credits |
| `order`      | apply order (lowest first) |
| `configPath` | the live value store, e.g. `%LOCALAPPDATA%\LeafItAloneSafe\bag-capacity.json` (`%VAR%` tokens expand at runtime) |
| `config`     | `{ "fields": [...] }` — the editor schema (below) |
| `apply`      | `{ "script": "apply.ps1", "args": ["-Yes"] }` — run via the subprocess runner |
| `revert`     | `{ "script": "revert.ps1", "args": ["-Yes"] }` |
| `requires`   | `{ "safeBase": true, "gameNotRunning": true, "python": true }` |

Each entry in `config.fields` describes one editable value:

| Key       | Meaning |
|-----------|---------|
| `key`     | dotted path into the live JSON (`multiplier`, `sizes.small`) |
| `label`   | UI label |
| `type`    | `int` \| `float` \| `bool` \| `enum` \| `text` |
| `min`/`max`/`step` | numeric bounds (int/float) |
| `default` | fallback when the live config lacks the key |
| `help`    | tooltip |
| `options` | allowed values (`type: enum`) |
| `when`    | `{ "key": "...", "equals": ... }` conditional visibility |

The manifest only *describes* the config file; `apply.ps1` remains the source of
truth for reading it, and the launcher preserves any keys the schema does not
mention (`_comment`, `_applyHelper`, …). `content-mods/bag-capacity/mod.json` is the
reference implementation and `content-mods/more-leaves/mod.json` is the second — added
with **no launcher code**, which is the point of the manifest-driven design.
