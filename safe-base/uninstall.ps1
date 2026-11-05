# uninstall.ps1 — reverses safe-base: restores the real steam_api64.dll from the
# backup first, then removes the firewall rule LAST so the network stays blocked
# until the file state is back to vanilla. Refuses if content mods are still
# registered (that check kicks in from M5).
#
# Self-elevates via UAC.

[CmdletBinding()]
param(
    [switch]$Yes,
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"

# ---------------------------------------------------------------------------
# Non-elevated: confirm + relaunch as admin.
# ---------------------------------------------------------------------------
if (-not (Test-IsAdmin)) {
    if (-not $Yes) {
        Write-Host ""
        Write-Host "This will:"
        Write-Host "  - restore the real steam_api64.dll (from backup)"
        Write-Host "  - remove the firewall rule '$RuleName'"
        Write-Host "  - remove the manifest at $ManifestPath"
        $reply = Read-Host "Type YES to continue (anything else aborts)"
        if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
    }
    $q = '"'
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File ${q}$PSCommandPath${q} -Elevated -Yes"
    $p = Start-Process -FilePath 'powershell.exe' -Verb RunAs -PassThru -ArgumentList $argLine
    $p.WaitForExit()
    exit $p.ExitCode
}

# ---- read the manifest ----
$m = $null
$haveManifest = Test-Path -LiteralPath $ManifestPath
if ($haveManifest) {
    try { $m = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json }
    catch { Write-Warning "Manifest at $ManifestPath is unreadable; will still try to remove rule + backup file." }
}

# ---- content-mod guard (fires from M5 when mods actually register) ----
if ($m -and $m.mods -and @($m.mods).Count -gt 0) {
    $list = (@($m.mods | ForEach-Object { "- $($_.name)" }) -join "`n")
    Write-Error "Content mods are still registered with safe-base:`n$list`n`nRevert them first (each mod's revert.ps1), or run uninstall-all.ps1 (M5) which reverts every registered mod and then uninstalls the base."
    exit 5
}

if (-not $Yes) {
    Write-Host ""
    Write-Host "Removing safe-base."
    if ($m -and $m.steamProxy) {
        Write-Host "  1. Restore real steam_api64.dll from backup."
        Write-Host "     backup : $($m.steamProxy.realDllBackupPath)"
        Write-Host "     target : $($m.steamProxy.realDllPath)"
    }
    Write-Host "  2. Remove firewall rule '$RuleName'."
    if ($haveManifest) { Write-Host "  3. Remove manifest." }
    Write-Host ""
    Write-Host "After uninstall, the game is fully online again. Any submissions"
    Write-Host "queued while modded (or in the save file used during modded play)"
    Write-Host "can leak to leaderboards on the next online run. Save isolation"
    Write-Host "with a purge-on-uninstall is M3."
    $reply = Read-Host "Type YES to continue"
    if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
}

# ---- STEP 1: restore real steam_api64.dll ----
if ($m -and $m.steamProxy) {
    Write-Host "[M2] Restoring real steam_api64.dll..." -ForegroundColor Cyan
    $ok = Restore-SteamProxy `
        -RealDllPath      $m.steamProxy.realDllPath `
        -BackupPath       $m.steamProxy.realDllBackupPath `
        -ExpectedProxySha $m.steamProxy.proxySha256
    if ($ok) {
        Write-Host "       restored to $($m.steamProxy.realDllPath)"
    } else {
        Write-Warning "Restore did not complete cleanly. Backup preserved at $($m.steamProxy.realDllBackupPath) (or the DLL at that path wasn't our proxy). Investigate before continuing."
    }
} else {
    Write-Host "[M2] No steam_proxy entry in manifest; nothing to restore." -ForegroundColor Yellow
}

# ---- STEP 2: remove firewall rule (LAST, so file state is vanilla first) ----
$state = Get-FirewallState
if ($state.Rule) {
    Write-Host "[M1] Removing firewall rule..." -ForegroundColor Cyan
    Remove-NetFirewallRule -Name $RuleName
    Write-Host "       removed: $RuleName"
} else {
    Write-Host "[M1] Rule '$RuleName' was not present (nothing to remove)." -ForegroundColor Yellow
}

# ---- STEP 3: remove manifest ----
if ($haveManifest) {
    Remove-Item -LiteralPath $ManifestPath -Force
    Write-Host "       removed: $ManifestPath"
}

Write-Host ""
Write-Host "OK - safe-base uninstalled. Game is back to online behaviour." -ForegroundColor Green
Write-Host "If you had any content mods applied, further play WILL count toward"
Write-Host "leaderboards again. Verify with safe-base\status.ps1."

if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
