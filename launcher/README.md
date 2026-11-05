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

Milestones **L1–L4** are complete.

**L1** — WPF app that builds and runs; the subprocess `ProcessRunner` (PowerShell
`-File`, `ArgumentList` quoting, streamed Output log, timeout + exit code); mod
discovery from `content-mods/<id>/mod.json`; the manifest schema; `bag-capacity`
refactored to ship one; a read-only mod list.

**L2** — the **schema-driven editor**: selecting a mod renders one control per
manifest field by declared `type` (slider+box for int/float, checkbox for bool,
combo for enum, text box for text), with `when`-gated visibility (e.g. the bag
sizes only show in `mode: sizes`). Edits write the live
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

Still ahead: the safe-base **lifecycle + first-run wizard** (L5), the overlay slimdown
(L6), the **bundled Python runtime** (L7) and **distribution polish** (L8).


## Layout

```
launcher/
  LeafItAloneLauncher.sln      one solution, one project
  global.json                  pins the .NET 8 SDK so it builds offline against the
                               locally installed 8.0 targeting packs
  src/Launcher/                the WPF app
    App.xaml / MainWindow.xaml
    Models/                    ModManifest (+ ConfigField), ModInfo
    Services/                  RepoLocator, ProcessRunner, ModDiscovery, EnvPaths
    ViewModels/                MainViewModel + ObservableObject + RelayCommand
  tests/HeadlessChecks/        a console harness (no game, no UI) for L1 checks
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
reference implementation.
