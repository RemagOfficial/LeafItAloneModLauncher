# install.ps1 — safe-base: verified Windows Firewall outbound-block (M1) +
# steam_api64.dll stub proxy (M2) + save isolation (M3) + version.dll overlay
# with persistent banner and one-shot preconditions (M4) for Leaf it Alone.
#
# What it does:
#   1. Auto-detects Leaf it Alone.exe (override with -GameExe "C:\...\Leaf it Alone.exe").
#   2. Refuses if the game is currently running.
#   3. Self-elevates via UAC.
#   4. Adds a Windows Firewall rule "LeafItAloneSafe-BlockOutbound" blocking
#      ALL outbound TCP+UDP from the game's exe, on every profile. (M1)
#   5. Verifies the rule (present + enabled + Block + Outbound + MpsSvc running).
#   6. Locates steam_api64.dll under the game folder, moves the real DLL to a
#      backup under %LOCALAPPDATA%\LeafItAloneSafe\backup\, then drops the
#      stub proxy at the same path so the game's SteamAPI_InitFlat() call
#      returns false. (M2)
#   7. Verifies the DLL at that path classifies as "proxy".
#   8. Moves the game's Saves/ folder (Unity's LocalLow convention) into the
#      safe-base backup and creates an empty placeholder in its place. Any
#      Steam Cloud marker file (steam_autocloud.vdf) travels with the backup,
#      which means the modded play has no cloud identity to sync against.
#      Warns about Steam Cloud if the marker was present. (M3)
#   9. Drops ../overlay/version.dll into the game folder root and writes a
#      matching LeafOverlay.ini with [safebase] Enabled=true. The overlay
#      then draws a persistent "OFFLINE · MODDED PLAY · SCORES WILL NOT BE
#      SUBMITTED" banner in-game and runs a startup preflight (proxy DLL size,
#      Saves/ isolation marker, live connect(1.1.1.1:443) probe). Any prior
#      version.dll / LeafOverlay.ini in the game folder is backed up and
#      restored on uninstall. (M4)
#  10. Writes an install manifest at %LOCALAPPDATA%\LeafItAloneSafe\manifest.json
#      recording everything needed to reverse the install.
#
# Any step 6-10 failure triggers a rollback of prior steps (overlay + proxy
# removed, firewall rule removed, Saves/ restored if we got that far). Nothing
# is left half-installed.
#
# Reversible: run uninstall.ps1. Requires admin (self-elevates).

[CmdletBinding()]
param(
    [string]$GameExe,
    [string]$SavePath,       # override auto-detected save folder (advanced)
    [switch]$Yes,            # skip interactive confirmation (used after elevation hop)
    [switch]$Elevated,       # internal: set when re-launched as admin
    [switch]$SkipSteam,      # M1-only install, for debugging (does not drop proxy)
    [switch]$SkipSaves,      # M1+M2 install, does not touch Saves/
    [switch]$SkipOverlay,    # M1+M2+M3 install, does not drop version.dll
    [switch]$AllowMissingSaves   # fresh install, no Saves/ folder yet
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"

# ---------------------------------------------------------------------------
# Non-elevated: preview + confirm, then relaunch self as admin with -Elevated.
# ---------------------------------------------------------------------------
if (-not (Test-IsAdmin)) {
    if (-not $GameExe) {
        try { $GameExe = Find-GameExe } catch { Write-Error $_; exit 2 }
    }
    if (-not $SavePath) { $SavePath = Find-GameSaveFolder }
    $savesExist = Test-Path -LiteralPath $SavePath
    $cloudMarker = if ($savesExist) { Test-SteamCloudMarker -SavePath $SavePath } else { $false }

    Write-Host ""
    Write-Host "safe-base (M1 + M2 + M3 + M4) will do the following:" -ForegroundColor Cyan
    Write-Host "  1. Add a Windows Firewall OUTBOUND-BLOCK rule:"
    Write-Host "       name    : $RuleName"
    Write-Host "       display : $RuleDisplay"
    Write-Host "       program : $GameExe"
    Write-Host "       scope   : TCP + UDP, all profiles (Domain/Private/Public)"
    Write-Host "  2. Verify the rule + the Windows Firewall service are healthy."
    if (-not $SkipSteam) {
        Write-Host "  3. Back up the game's real steam_api64.dll to:"
        Write-Host "       $BackupDir\steam_api64.dll"
        Write-Host "     and replace it with the safe-base stub proxy."
        Write-Host "     Effect: SteamAPI_Init returns false; game thinks Steam"
        Write-Host "     is not running; no Steamworks upload path exists."
    }
    if (-not $SkipSaves) {
        Write-Host "  4. Move the game's Saves/ folder to"
        Write-Host "       $BackupDir\saves-<UTC timestamp>\"
        Write-Host "     and create an empty placeholder at:"
        Write-Host "       $SavePath"
        Write-Host "     Effect: modded play writes to a fresh, throwaway Saves/."
        Write-Host "     On uninstall the modded folder is PARKED (never deleted)"
        Write-Host "     and the original is restored in place."
        if ($savesExist -and $cloudMarker) {
            Write-Host ""
            Write-Host "  !!! STEAM CLOUD DETECTED !!!" -ForegroundColor Yellow
            Write-Host "  Saves/steam_autocloud.vdf is present, which means Steam"
            Write-Host "  Cloud sync is enabled for this game. Even with M2's proxy"
            Write-Host "  installed, the Steam client may still push or pull files"
            Write-Host "  in this folder on its own schedule."
            Write-Host "  RECOMMENDED: before playing modded, disable Steam Cloud"
            Write-Host "  for this game manually:"
            Write-Host "    Steam Library -> right-click Leaf it Alone -> Properties"
            Write-Host "    -> Updates -> uncheck 'Keep game saves in the Steam Cloud'"
            Write-Host "  safe-base will NOT edit your Steam config for you."
            Write-Host ""
        }
    }
    if (-not $SkipOverlay) {
        $gameFolder = Split-Path -Parent $GameExe
        Write-Host "  5. Drop safe-base's overlay into the game folder:"
        Write-Host "       $gameFolder\version.dll"
        Write-Host "       $gameFolder\LeafOverlay.ini    ([safebase] Enabled=true)"
        Write-Host "     The overlay draws a persistent 'OFFLINE - MODDED PLAY"
        Write-Host "     - SCORES WILL NOT BE SUBMITTED' banner and runs a"
        Write-Host "     one-shot preflight from inside the game's process."
        Write-Host "     Any existing version.dll / LeafOverlay.ini is backed"
        Write-Host "     up under $BackupDir\overlay\ and restored on uninstall."
    }
    Write-Host "  6. Write an install manifest to:"
    Write-Host "       $ManifestPath"
    Write-Host ""
    Write-Host "Reversible any time with: safe-base\uninstall.ps1"
    Write-Host "Firewall rule changes are the only OS-level change; game-folder"
    Write-Host "modifications are exactly one file (steam_api64.dll), one folder"
    Write-Host "rename (Saves -> backup, same drive = instant), and the overlay"
    Write-Host "pair (version.dll + LeafOverlay.ini). All reversible."
    Write-Host ""
    if (-not $Yes) {
        $reply = Read-Host "Type YES to continue (anything else aborts)"
        if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
    }
    Write-Host "Requesting administrator rights (UAC prompt)..." -ForegroundColor Yellow
    $q = '"'
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File ${q}$PSCommandPath${q} -Elevated -Yes -GameExe ${q}$GameExe${q} -SavePath ${q}$SavePath${q}"
    if ($SkipSteam)        { $argLine += " -SkipSteam" }
    if ($SkipSaves)        { $argLine += " -SkipSaves" }
    if ($SkipOverlay)      { $argLine += " -SkipOverlay" }
    if ($AllowMissingSaves){ $argLine += " -AllowMissingSaves" }
    try {
        $p = Start-Process -FilePath 'powershell.exe' -Verb RunAs -PassThru -ArgumentList $argLine
        $p.WaitForExit()
        exit $p.ExitCode
    } catch {
        Write-Error "Elevation denied or failed: $($_.Exception.Message)"
        exit 3
    }
}

# ---------------------------------------------------------------------------
# Elevated path: do the real work.
# ---------------------------------------------------------------------------
if (-not $GameExe) { $GameExe = Find-GameExe }
if (-not $SavePath) { $SavePath = Find-GameSaveFolder }

# Refuse double-install (would leave stale rule + orphan manifest otherwise).
if (Test-Path -LiteralPath $ManifestPath) {
    Write-Error "safe-base is already installed (manifest at $ManifestPath). Run uninstall.ps1 first if you want to reinstall."
    exit 4
}

# Refuse if the game is currently running: file-state changes under a live
# process are what corrupt saves.
$running = Get-Process -Name 'Leaf it Alone' -ErrorAction SilentlyContinue
if ($running) {
    Write-Error "Leaf it Alone is running (PID(s): $($running.Id -join ', ')). Close the game before installing safe-base."
    if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
    exit 5
}

# Show plan if we're here directly (not via the pre-elevate hop) and user hasn't passed -Yes.
if (-not $Yes) {
    Write-Host ""
    Write-Host "safe-base will add firewall rule '$RuleName' blocking outbound"
    Write-Host "TCP+UDP from: $GameExe"
    if (-not $SkipSteam) {
        Write-Host "and replace steam_api64.dll with the stub proxy (backup kept)."
    }
    if (-not $SkipSaves) {
        Write-Host "and move $SavePath to a timestamped backup, creating an empty placeholder."
    }
    if (-not $SkipOverlay) {
        Write-Host "and drop version.dll + LeafOverlay.ini into the game folder (persistent offline banner)."
    }
    Write-Host "Manifest will be written to: $ManifestPath"
    Write-Host "Undo with: safe-base\uninstall.ps1"
    $reply = Read-Host "Type YES to continue"
    if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
}

# Any pre-existing rule under the same name (say, from a partial install) gets replaced.
if (Get-NetFirewallRule -Name $RuleName -ErrorAction SilentlyContinue) {
    Write-Warning "Pre-existing rule '$RuleName' will be removed and re-added."
    Remove-NetFirewallRule -Name $RuleName
}

Write-Host "[M1] Adding firewall rule..." -ForegroundColor Cyan
New-NetFirewallRule `
    -Name         $RuleName `
    -DisplayName  $RuleDisplay `
    -Description  $RuleDesc `
    -Direction    Outbound `
    -Action       Block `
    -Program      $GameExe `
    -Protocol     Any `
    -Profile      Any `
    -Enabled      True | Out-Null

# ---- M1 verify ----
$state = Get-FirewallState
if (-not $state.Rule) { throw "M1 verification failed: rule '$RuleName' not present after creation" }
if ($state.Rule.Enabled -ne 'True') { throw "M1 verification failed: rule is not enabled" }
if ($state.Rule.Action -ne 'Block') { throw "M1 verification failed: rule action is $($state.Rule.Action), expected Block" }
if ($state.Rule.Direction -ne 'Outbound') { throw "M1 verification failed: rule direction is $($state.Rule.Direction), expected Outbound" }
if ($state.FwServiceStat -ne 'Running') {
    Write-Warning "Windows Firewall service (MpsSvc) is currently '$($state.FwServiceStat)'. The rule is created but nothing is enforcing it. Start the service (services.msc -> Windows Defender Firewall -> Start)."
}

# Helper used by M2/M3/M4 catch blocks: fully unwind whatever succeeded so far
# so nothing is left half-installed.
function Rollback-Install {
    param($steamInfo, $saveInfo, $overlayInfo)
    if ($overlayInfo) {
        try {
            if (Test-Path -LiteralPath $overlayInfo.versionDllPath) {
                Remove-Item -LiteralPath $overlayInfo.versionDllPath -Force
            }
            if (Test-Path -LiteralPath $overlayInfo.overlayIniPath) {
                Remove-Item -LiteralPath $overlayInfo.overlayIniPath -Force
            }
            if ($overlayInfo.priorVersionBackup -and (Test-Path -LiteralPath $overlayInfo.priorVersionBackup)) {
                Move-Item -LiteralPath $overlayInfo.priorVersionBackup -Destination $overlayInfo.versionDllPath -Force
            }
            if ($overlayInfo.priorIniBackup -and (Test-Path -LiteralPath $overlayInfo.priorIniBackup)) {
                Move-Item -LiteralPath $overlayInfo.priorIniBackup -Destination $overlayInfo.overlayIniPath -Force
            }
            Write-Host "  - rolled back M4 (removed our version.dll + INI, restored any prior)" -ForegroundColor DarkYellow
        } catch { Write-Warning "M4 rollback failed: $($_.Exception.Message)" }
    }
    if ($saveInfo -and $saveInfo.backupPath -and (Test-Path -LiteralPath $saveInfo.backupPath)) {
        # Restore original Saves/, park modded (which is likely empty at this
        # stage since we just moved it).
        try {
            if (Test-Path -LiteralPath $saveInfo.originalPath) {
                Remove-Item -LiteralPath $saveInfo.originalPath -Recurse -Force
            }
            Move-Item -LiteralPath $saveInfo.backupPath -Destination $saveInfo.originalPath -Force
            Write-Host "  - rolled back M3 (restored Saves/)" -ForegroundColor DarkYellow
        } catch { Write-Warning "M3 rollback failed: $($_.Exception.Message)" }
    }
    if ($steamInfo) {
        try {
            if (Test-Path -LiteralPath $steamInfo.realDllPath) {
                Remove-Item -LiteralPath $steamInfo.realDllPath -Force
            }
            Move-Item -LiteralPath $steamInfo.realDllBackupPath -Destination $steamInfo.realDllPath -Force
            Write-Host "  - rolled back M2 (restored real steam_api64.dll)" -ForegroundColor DarkYellow
        } catch { Write-Warning "M2 rollback failed: $($_.Exception.Message)" }
    }
    Remove-NetFirewallRule -Name $RuleName -ErrorAction SilentlyContinue
    Write-Host "  - rolled back M1 (removed firewall rule)" -ForegroundColor DarkYellow
}

# ---- M2 - steam proxy ----
$steamInfo = $null
if (-not $SkipSteam) {
    try {
        Write-Host "[M2] Locating steam_api64.dll..." -ForegroundColor Cyan
        $steamPath = Find-GameSteamDll -GameExePath $GameExe
        Write-Host "       found at: $steamPath"

        if (-not (Test-Path -LiteralPath $SteamProxySource)) {
            throw "proxy DLL missing at $SteamProxySource - run safe-base\steam_proxy\build_steam_proxy.ps1 first"
        }

        Write-Host "[M2] Installing proxy (real DLL backed up to $BackupDir)..." -ForegroundColor Cyan
        $steamInfo = Install-SteamProxy -RealDllPath $steamPath -ProxySource $SteamProxySource

        $kind = Classify-SteamDll -Path $steamInfo.realDllPath -ProxySha $steamInfo.proxySha256
        if ($kind -ne 'proxy') {
            throw "M2 verification failed: steam_api64.dll at '$($steamInfo.realDllPath)' classifies as '$kind', expected 'proxy'"
        }
        Write-Host "       proxy active ($($steamInfo.proxySize) bytes, sha256 $($steamInfo.proxySha256.Substring(0,12))...)"
    } catch {
        Write-Error "M2 failed: $($_.Exception.Message)"
        Write-Host "Rolling back prior steps so state stays coherent." -ForegroundColor Yellow
        Rollback-Install -steamInfo $steamInfo -saveInfo $null
        if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
        exit 6
    }
} else {
    Write-Warning "[M2] -SkipSteam set: not installing steam_api64.dll proxy."
}

# ---- M3 - save isolation ----
$saveInfo = $null
if (-not $SkipSaves) {
    try {
        Write-Host "[M3] Isolating saves..." -ForegroundColor Cyan
        Write-Host "       source: $SavePath"
        if (-not (Test-Path -LiteralPath $SavePath)) {
            if (-not $AllowMissingSaves) {
                throw "Saves/ folder not found at $SavePath. Play the game once and save, or pass -AllowMissingSaves."
            }
            Write-Host "       Saves/ does not exist yet (-AllowMissingSaves set). Creating placeholder only."
        }
        $saveInfo = Install-SaveIsolation -SavePath $SavePath -AllowMissing:$AllowMissingSaves
        Write-Host "       backup: $($saveInfo.backupPath)"
        Write-Host "       pre-move content: $($saveInfo.preMoveCount) file(s), $($saveInfo.preMoveBytes) bytes"
        if ($saveInfo.steamCloudVdf) {
            Write-Host ""
            Write-Host "  !!! Steam Cloud marker (steam_autocloud.vdf) was present. It moved with the backup." -ForegroundColor Yellow
            Write-Host "  Before playing modded, disable Steam Cloud for this game:"
            Write-Host "    Steam Library -> right-click Leaf it Alone -> Properties -> Updates"
            Write-Host "    -> uncheck 'Keep game saves in the Steam Cloud'"
        }
    } catch {
        Write-Error "M3 failed: $($_.Exception.Message)"
        Write-Host "Rolling back prior steps." -ForegroundColor Yellow
        Rollback-Install -steamInfo $steamInfo -saveInfo $saveInfo
        if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
        exit 7
    }
} else {
    Write-Warning "[M3] -SkipSaves set: not isolating Saves/ folder."
}

# ---- M4 - version.dll overlay ----
$overlayInfo = $null
if (-not $SkipOverlay) {
    try {
        Write-Host "[M4] Installing persistent-banner overlay (version.dll)..." -ForegroundColor Cyan
        $proxySize = if ($steamInfo) { [long]$steamInfo.proxySize } else { 0 }
        $overlayInfo = Install-VersionOverlay `
            -GameExePath $GameExe `
            -ExpectedSteamProxySize $proxySize
        $slot = Find-GameOverlaySlot -GameExePath $GameExe
        Write-Host "       version.dll -> $($overlayInfo.versionDllPath) ($($overlayInfo.sourceDllSize) bytes)"
        Write-Host "       LeafOverlay.ini -> $($overlayInfo.overlayIniPath)"
        if ($proxySize -gt 0) {
            Write-Host "       startup check will expect steam_api64.dll size = $proxySize bytes"
        } else {
            Write-Host "       -SkipSteam set: startup size check disabled (existence only)"
        }
    } catch {
        Write-Error "M4 failed: $($_.Exception.Message)"
        Write-Host "Rolling back prior steps." -ForegroundColor Yellow
        Rollback-Install -steamInfo $steamInfo -saveInfo $saveInfo -overlayInfo $overlayInfo
        if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
        exit 8
    }
} else {
    Write-Warning "[M4] -SkipOverlay set: not installing version.dll banner overlay."
}

# ---- manifest ----
New-Item -Path $StateDir -ItemType Directory -Force | Out-Null
$milestones = @('M1')
if (-not $SkipSteam)   { $milestones += 'M2' }
if (-not $SkipSaves)   { $milestones += 'M3' }
if (-not $SkipOverlay) { $milestones += 'M4' }
$milestone = $milestones -join '+'

$manifest = [ordered]@{
    schemaVersion   = 4
    milestone       = $milestone
    installedAt     = (Get-Date).ToUniversalTime().ToString('o')
    gameExe         = $GameExe
    ruleName        = $RuleName
    ruleVerify      = [ordered]@{
        present   = $true
        enabled   = ($state.Rule.Enabled -eq 'True')
        action    = "$($state.Rule.Action)"
        direction = "$($state.Rule.Direction)"
        profiles  = "$($state.Rule.Profile)"
        targets   = $state.Program
        fwService = $state.FwServiceStat
    }
    steamProxy      = if ($steamInfo) { [ordered]@{
        realDllPath       = $steamInfo.realDllPath
        realDllBackupPath = $steamInfo.realDllBackupPath
        realDllSize       = $steamInfo.realDllSize
        realDllSha256     = $steamInfo.realDllSha256
        proxySourcePath   = $steamInfo.proxySourcePath
        proxySize         = $steamInfo.proxySize
        proxySha256       = $steamInfo.proxySha256
        installedAt       = $steamInfo.installedAt
    } } else { $null }
    saveIsolation   = if ($saveInfo) { [ordered]@{
        originalPath   = $saveInfo.originalPath
        backupPath     = $saveInfo.backupPath
        movedAt        = $saveInfo.movedAt
        preMoveExists  = $saveInfo.preMoveExists
        preMoveCount   = $saveInfo.preMoveCount
        preMoveBytes   = $saveInfo.preMoveBytes
        steamCloudVdf  = $saveInfo.steamCloudVdf
        preMoveFiles   = $saveInfo.preMoveFiles
    } } else { $null }
    versionOverlay  = if ($overlayInfo) { [ordered]@{
        gameFolder         = $overlayInfo.gameFolder
        versionDllPath     = $overlayInfo.versionDllPath
        overlayIniPath     = $overlayInfo.overlayIniPath
        sourceDllPath      = $overlayInfo.sourceDllPath
        sourceDllSize      = $overlayInfo.sourceDllSize
        sourceDllSha256    = $overlayInfo.sourceDllSha256
        expectedProxySize  = $overlayInfo.expectedProxySize
        backupDir          = $overlayInfo.backupDir
        priorVersionDll    = $overlayInfo.priorVersionDll
        priorVersionBackup = $overlayInfo.priorVersionBackup
        priorIni           = $overlayInfo.priorIni
        priorIniBackup     = $overlayInfo.priorIniBackup
        installedAt        = $overlayInfo.installedAt
    } } else { $null }
    mods            = @()     # M5+ content mods registered here
}
($manifest | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath $ManifestPath -Encoding UTF8

Write-Host ""
Write-Host "OK - safe-base installed ($milestone)." -ForegroundColor Green
Write-Host "  Rule       : $RuleName  (Block, Outbound, Profile: $($state.Rule.Profile))"
Write-Host "  Program    : $GameExe"
Write-Host "  Firewall   : MpsSvc = $($state.FwServiceStat)"
if ($steamInfo) {
    Write-Host "  Steam DLL  : $($steamInfo.realDllPath)"
    Write-Host "               proxy = $($steamInfo.proxySize) bytes"
    Write-Host "               backup = $($steamInfo.realDllBackupPath) ($($steamInfo.realDllSize) bytes)"
}
if ($saveInfo) {
    Write-Host "  Saves      : $($saveInfo.originalPath)"
    Write-Host "               backup = $($saveInfo.backupPath)"
    Write-Host "               pre-move: $($saveInfo.preMoveCount) file(s), $($saveInfo.preMoveBytes) bytes"
}
if ($overlayInfo) {
    Write-Host "  Overlay    : $($overlayInfo.versionDllPath)"
    Write-Host "               INI = $($overlayInfo.overlayIniPath)"
    Write-Host "               banner will show a green strip when all checks pass,"
    Write-Host "               a red one when any fails. Log: $((Split-Path -Parent $overlayInfo.versionDllPath))\LeafOverlay.log"
}
Write-Host "  Manifest   : $ManifestPath"
Write-Host ""
Write-Host "Test it: launch the game. Steam will still start it (loopback IPC"
Write-Host "in a separate process). Inside the game, the Steamworks layer should"
Write-Host "come up in a 'no Steam' or offline state - achievements, stats and"
Write-Host "leaderboards stop working, because SteamAPI_Init returned false."
Write-Host "The game will also start with no prior saves (Saves/ was moved aside),"
Write-Host "so any progression is written to a throwaway folder."
if ($overlayInfo) {
    Write-Host "The persistent OFFLINE banner across the top-centre of the screen"
    Write-Host "is drawn by safe-base's overlay DLL. Green means every preflight"
    Write-Host "check passed; red means one failed and the reason is on the"
    Write-Host "banner and in LeafOverlay.log. The F9 toggle hides the HUD panel"
    Write-Host "but NOT the banner."
}
Write-Host ""
Write-Host "If the game refuses to launch or crashes: run safe-base\uninstall.ps1."
Write-Host "It will restore your pre-existing version.dll (or remove ours),"
Write-Host "restore the real steam_api64.dll, restore your Saves/ folder,"
Write-Host "and remove the firewall rule (parking the modded Saves/ alongside)."
Write-Host ""
Write-Host "Check any time with: safe-base\status.ps1"

# Keep the UAC-spawned window open so the user can read this.
if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
