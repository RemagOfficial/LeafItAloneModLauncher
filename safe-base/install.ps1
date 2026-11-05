# install.ps1 — safe-base: verified Windows Firewall outbound-block (M1) +
# steam_api64.dll stub proxy (M2) for Leaf it Alone.
#
# What it does:
#   1. Auto-detects Leaf it Alone.exe (override with -GameExe "C:\...\Leaf it Alone.exe").
#   2. Self-elevates via UAC.
#   3. Adds a Windows Firewall rule "LeafItAloneSafe-BlockOutbound" blocking
#      ALL outbound TCP+UDP from the game's exe, on every profile. (M1)
#   4. Verifies the rule (present + enabled + Block + Outbound + MpsSvc running).
#   5. Locates steam_api64.dll under the game folder, moves the real DLL to a
#      backup under %LOCALAPPDATA%\LeafItAloneSafe\backup\, then drops the
#      stub proxy at the same path so the game's SteamAPI_InitFlat() call
#      returns false. (M2)
#   6. Verifies the DLL at that path classifies as "proxy" (hash matches our
#      source in safe-base\steam_proxy\steam_api64.dll).
#   7. Writes an install manifest at %LOCALAPPDATA%\LeafItAloneSafe\manifest.json
#      recording everything needed to reverse the install, and to let later
#      milestones (M3-M5) and content mods see the base is active.
#
# Any step 5-6 failure triggers a rollback of step 3 (rule removed). Nothing
# is left half-installed.
#
# Reversible: run uninstall.ps1.
# Requires admin (self-elevates).

[CmdletBinding()]
param(
    [string]$GameExe,
    [switch]$Yes,          # skip interactive confirmation (used after elevation hop)
    [switch]$Elevated,     # internal: set when re-launched as admin
    [switch]$SkipSteam     # M1-only install, for debugging (does not drop proxy)
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
    Write-Host ""
    Write-Host "safe-base (M1 + M2) will do the following:" -ForegroundColor Cyan
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
    Write-Host "  4. Write an install manifest to:"
    Write-Host "       $ManifestPath"
    Write-Host ""
    Write-Host "Reversible any time with: safe-base\uninstall.ps1"
    Write-Host "Firewall rule changes are the only OS-level change; the game"
    Write-Host "folder modification is exactly one file (steam_api64.dll)."
    Write-Host ""
    if (-not $Yes) {
        $reply = Read-Host "Type YES to continue (anything else aborts)"
        if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
    }
    Write-Host "Requesting administrator rights (UAC prompt)..." -ForegroundColor Yellow
    $q = '"'
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File ${q}$PSCommandPath${q} -Elevated -Yes -GameExe ${q}$GameExe${q}"
    if ($SkipSteam) { $argLine += " -SkipSteam" }
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

# Refuse double-install (would leave stale rule + orphan manifest otherwise).
if (Test-Path -LiteralPath $ManifestPath) {
    Write-Error "safe-base is already installed (manifest at $ManifestPath). Run uninstall.ps1 first if you want to reinstall."
    exit 4
}

# Show plan if we're here directly (not via the pre-elevate hop) and user hasn't passed -Yes.
if (-not $Yes) {
    Write-Host ""
    Write-Host "safe-base will add firewall rule '$RuleName' blocking outbound"
    Write-Host "TCP+UDP from: $GameExe"
    if (-not $SkipSteam) {
        Write-Host "and replace steam_api64.dll with the stub proxy (backup kept)."
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

        # Re-read the DLL at the game's path and confirm it's our proxy.
        $kind = Classify-SteamDll -Path $steamInfo.realDllPath -ProxySha $steamInfo.proxySha256
        if ($kind -ne 'proxy') {
            throw "M2 verification failed: steam_api64.dll at '$($steamInfo.realDllPath)' classifies as '$kind', expected 'proxy'"
        }
        Write-Host "       proxy active ($($steamInfo.proxySize) bytes, sha256 $($steamInfo.proxySha256.Substring(0,12))...)"
    } catch {
        Write-Error "M2 failed: $($_.Exception.Message)"
        Write-Host "Rolling back M1 (removing firewall rule) so state stays coherent." -ForegroundColor Yellow
        Remove-NetFirewallRule -Name $RuleName -ErrorAction SilentlyContinue
        exit 6
    }
} else {
    Write-Warning "[M2] -SkipSteam set: not installing steam_api64.dll proxy. Steamworks uploads will NOT be blocked."
}

# ---- manifest ----
New-Item -Path $StateDir -ItemType Directory -Force | Out-Null
$milestone = if ($SkipSteam) { 'M1' } else { 'M2' }
$manifest = [ordered]@{
    schemaVersion   = 2
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
    saveIsolation   = $null   # M3
    overlayDll      = $null   # M4
    mods            = @()     # M5+ content mods registered here
}
($manifest | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $ManifestPath -Encoding UTF8

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
Write-Host "  Manifest   : $ManifestPath"
Write-Host ""
Write-Host "Test it: launch the game. Steam will still start it (loopback IPC"
Write-Host "in a separate process). Inside the game, the Steamworks layer should"
Write-Host "come up in a 'no Steam' or offline state - achievements, stats and"
Write-Host "leaderboards stop working, because SteamAPI_Init returned false."
Write-Host ""
Write-Host "If the game refuses to launch or crashes: run safe-base\uninstall.ps1."
Write-Host "It will restore the real steam_api64.dll and remove the firewall rule."
Write-Host ""
Write-Host "Check any time with: safe-base\status.ps1"

# Keep the UAC-spawned window open so the user can read this.
if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
