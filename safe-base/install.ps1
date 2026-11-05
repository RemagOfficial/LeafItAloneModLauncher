# install.ps1 — safe-base M1: verified Windows Firewall outbound-block for Leaf it Alone.exe.
#
# What it does (M1 only):
#   1. Auto-detects Leaf it Alone.exe (override with -GameExe "C:\...\Leaf it Alone.exe").
#   2. Self-elevates via UAC.
#   3. Adds a Windows Firewall rule named "LeafItAloneSafe-BlockOutbound" that blocks
#      ALL outbound TCP + UDP from the game's exe, on every network profile.
#   4. Verifies the rule is present, enabled, targeting the right exe, and that the
#      Windows Firewall service (MpsSvc) is actually running.
#   5. Writes an install manifest to %LOCALAPPDATA%\LeafItAloneSafe\manifest.json
#      so later milestones + content mods can see the base is active.
#
# Reversible: run uninstall.ps1.
#
# Requires admin (self-elevates).

[CmdletBinding()]
param(
    [string]$GameExe,
    [switch]$Yes,          # skip interactive confirmation (used after elevation hop)
    [switch]$Elevated      # internal: set when re-launched as admin
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
    Write-Host "safe-base M1 will do the following:" -ForegroundColor Cyan
    Write-Host "  1. Add a Windows Firewall OUTBOUND-BLOCK rule:"
    Write-Host "       name    : $RuleName"
    Write-Host "       display : $RuleDisplay"
    Write-Host "       program : $GameExe"
    Write-Host "       scope   : TCP + UDP, all profiles (Domain/Private/Public)"
    Write-Host "  2. Verify the rule + the Windows Firewall service are healthy."
    Write-Host "  3. Write an install manifest to:"
    Write-Host "       $ManifestPath"
    Write-Host ""
    Write-Host "This does NOT touch the game folder, saves, DLLs, or Steam."
    Write-Host "Undo any time by running: safe-base\uninstall.ps1"
    Write-Host ""
    if (-not $Yes) {
        $reply = Read-Host "Type YES to continue (anything else aborts)"
        if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
    }
    Write-Host "Requesting administrator rights (UAC prompt)..." -ForegroundColor Yellow
    $q = '"'
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File ${q}$PSCommandPath${q} -Elevated -Yes -GameExe ${q}$GameExe${q}"
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
    Write-Host "safe-base M1 will add firewall rule '$RuleName' blocking outbound TCP+UDP from:"
    Write-Host "  $GameExe"
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

Write-Host "Adding firewall rule..." -ForegroundColor Cyan
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

# ---- verify ----
$state = Get-FirewallState
if (-not $state.Rule) { throw "verification failed: rule '$RuleName' not present after creation" }
if ($state.Rule.Enabled -ne 'True') { throw "verification failed: rule is not enabled" }
if ($state.Rule.Action -ne 'Block') { throw "verification failed: rule action is $($state.Rule.Action), expected Block" }
if ($state.Rule.Direction -ne 'Outbound') { throw "verification failed: rule direction is $($state.Rule.Direction), expected Outbound" }

if ($state.FwServiceStat -ne 'Running') {
    Write-Warning "Windows Firewall service (MpsSvc) is currently '$($state.FwServiceStat)'. The rule is created but nothing is enforcing it. Start the service (services.msc -> Windows Defender Firewall -> Start) and re-run status.ps1."
}

# ---- manifest ----
New-Item -Path $StateDir -ItemType Directory -Force | Out-Null
$manifest = [ordered]@{
    schemaVersion   = 1
    milestone       = 'M1'
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
    saveIsolation   = $null   # M3
    steamProxy      = $null   # M2
    overlayDll      = $null   # M4
    mods            = @()     # M5+ content mods registered here
}
($manifest | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $ManifestPath -Encoding UTF8

Write-Host ""
Write-Host "OK - safe-base M1 installed." -ForegroundColor Green
Write-Host "  Rule      : $RuleName  (Block, Outbound, Profile: $($state.Rule.Profile))"
Write-Host "  Program   : $GameExe"
Write-Host "  Firewall  : MpsSvc = $($state.FwServiceStat)"
Write-Host "  Manifest  : $ManifestPath"
Write-Host ""
Write-Host "Test it: launch the game; try anything that hits the network (leaderboards view"
Write-Host "etc.). It should fail to load. Steam still launches the game fine because that"
Write-Host "traffic is loopback IPC in a separate process."
Write-Host ""
Write-Host "Check any time with: safe-base\status.ps1"
Write-Host "Undo with:             safe-base\uninstall.ps1"

# Keep the UAC-spawned window open so the user can read this.
if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
