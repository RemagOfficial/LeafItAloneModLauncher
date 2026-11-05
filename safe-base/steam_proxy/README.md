# `safe-base/steam_proxy/` — the stub `steam_api64.dll`

A drop-in replacement for the game's real `steam_api64.dll`. It exports the
**same 1089 names**, every function returns `0` (which is `false` / `NULL` /
`HSteamPipe = 0`), and `g_pSteamClientGameServer` is a null pointer.

The game calls `SteamAPI_Init()` (an inline wrapper around
`SteamAPI_InitFlat(STEAMAPI_VERSION)`, per the modern Steamworks SDK). That
call lands on our stub, returns `false`, and `com.rlabrecque.steamworks.net.dll`
never obtains an `ISteamUserStats` interface — so `UploadLeaderboardScore`,
`SetStat`, `SetAchievement`, cloud saves, etc. **cannot happen** from the game's
process. The Steamworks code path in the game enters its "Steam not running"
branch instead.

## Files

| File | Role |
|---|---|
| `exports.txt` | one export name per line — the source of truth. Regenerate with `dumpbin /exports <real steam_api64.dll>`. |
| `generate_proxy.ps1` | produces `steam_proxy.cpp` + `steam_proxy.def` from `exports.txt`. Deterministic. |
| `steam_proxy.cpp` | generated — 1089 `STUB_FN(name)` macros expanding to `extern "C" __declspec(dllexport) unsigned long long name(void) { return 0; }` plus one data export. |
| `steam_proxy.def` | generated — `LIBRARY steam_api64` + `EXPORTS` + 1089 names. |
| `build_steam_proxy.ps1` | MSVC x64 build → `steam_api64.dll`. |
| `steam_api64.dll` | the built proxy. Ships in the repo (like `version.dll` in `overlay/`). |

## Rebuilding

You only need to rebuild if the game's Steamworks SDK version changes and
exports different names. In that case:

```powershell
# 1. Point this at the game's current real steam_api64.dll.
$real = "B:\SteamLibrary\steamapps\common\Leaf it Alone\Leaf it Alone_Data\Plugins\x86_64\steam_api64.dll"

# 2. Refresh exports.txt (assumes MSVC tools on PATH via a vcvars64 shell,
#    or use safe-base/../list_steam_exports.ps1 for a self-contained version).
& dumpbin /exports $real > exports.txt.new

# 3. Diff new vs old list, and manually curate exports.txt (keep only the raw
#    name column, one per line, sorted).

# 4. Regenerate the sources and rebuild.
.\generate_proxy.ps1
.\build_steam_proxy.ps1
```

## Design notes / gotchas

- **x64 calling convention** means declaring `(void)` on a stub is safe even
  when the caller passes 4 register args + shadow space. The callee simply
  ignores them and returns 0 in `RAX`.
- **Struct returns** larger than 8 bytes use a hidden pointer in `RCX`. Our
  stub doesn't touch that buffer, so the game sees whatever it pre-zeroed.
  Because `SteamAPI_Init` fails, none of these calls happen anyway.
- **`g_pSteamClientGameServer`** is exported as *data* in the real DLL, not a
  function. We match that (`void* = nullptr`) so an import expecting a data
  symbol resolves correctly.
- **MSVC linker warning LNK4197** ("export specified multiple times") fires
  once during build because we declare the data export both via
  `__declspec(dllexport)` and in the `.def`. Harmless — linker uses the first
  spec. Fix by removing one of the two declarations.
- **We deliberately do not forward anything to the real DLL.** No lazy
  loading, no passthrough for the "harmless" calls. The whole point is that
  nothing from the game ever reaches Steam. **Tested 2026-11-05:** the game
  launches and plays normally with the pure-stub proxy in place — no
  "Steam required" prompt, no crash. The in-game leaderboard shows empty
  (the game's "Steam unavailable" branch handles it), so no compat-mode
  variant is needed. If a future game update starts hard-depending on a
  specific Steam feature for single-player (Steam Input for controllers,
  say), we'd add a compat-mode proxy behind a manifest flag at that point.

## What this does NOT block

- Any traffic from **`Steam.exe`** itself (the client process is a separate
  executable; the game's rule doesn't apply and neither does this DLL — the
  client is not running our proxy). This proxy stops the *game* from
  *asking* Steam to do things. Steam's own outbound sync of what it already
  knew about is unaffected.
- Steam client's own cloud-sync schedule for the game's `Saves/` folder
  (`steam_autocloud.vdf` marks the folder as a cloud-sync target). Even with
  this proxy installed, Steam may still push or pull files there on its own
  timer.

**M3 (save isolation) covers the residual case** by moving the entire
`Saves/` folder into `%LOCALAPPDATA%\LeafItAloneSafe\backup\` before the
game starts, and creating an empty placeholder. The `steam_autocloud.vdf`
marker moves with the backup, so the placeholder has no cloud identity for
Steam to sync against. Install also warns you to disable Steam Cloud
manually if the marker was present.

## Verifying manually

```powershell
# After install.ps1, this file exists at the game's Plugins path.
$dll = "B:\SteamLibrary\steamapps\common\Leaf it Alone\Leaf it Alone_Data\Plugins\x86_64\steam_api64.dll"
Get-Item $dll | Format-List Length, LastWriteTime
# Should be ~162 KB (proxy), not ~320 KB (real).

# And its SHA matches the source in the repo:
(Get-FileHash $dll -Algorithm SHA256).Hash
(Get-FileHash .\safe-base\steam_proxy\steam_api64.dll -Algorithm SHA256).Hash
# Both should equal the manifest's steamProxy.proxySha256.
```

Or just run `safe-base\status.ps1`.
