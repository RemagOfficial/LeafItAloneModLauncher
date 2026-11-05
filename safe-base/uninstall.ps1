# uninstall.ps1 — reverses safe-base:
#   * Refuses if Leaf it Alone is running (never mutate live files).
#   * Restores the game's real Saves/ folder first (the modded Saves/ is
#     PARKED under %LOCALAPPDATA%\LeafItAloneSafe\backup\saves-modded-<ts>\,
#     never deleted, so nothing is lost).
#   * Then restores the real steam_api64.dll from backup.
#   * Finally removes the firewall rule LAST, so the network stays blocked
#     until file state is fully vanilla.
#   * Removes the manifest.
#
# Refuses if content mods are still registered (guard is live; the registry
# fills up in M5).
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
        Write-Host "  - restore the game's real Saves/ folder (modded Saves/ is"
        Write-Host "    PARKED under $BackupDir\saves-modded-<ts>\, never deleted)"
        Write-Host "  - restore the real steam_api64.dll (from backup)"
        Write-Host "  - remove the firewall rule '$RuleName' (LAST)"
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
    catch { Write-Warning "Manifest at $ManifestPath is unreadable; will still try to remove rule + restore known backups." }
}

# ---- game-running guard (never mutate live files) ----
$running = Get-Process -Name 'Leaf it Alone' -ErrorAction SilentlyContinue
if ($running) {
    Write-Error "Leaf it Alone is running (PID(s): $($running.Id -join ', ')). Close the game before uninstalling safe-base. Modded saves may still be in use."
    if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
    exit 6
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
    $n = 0
    if ($m -and $m.saveIsolation) {
        $n++
        Write-Host "  $n. Restore Saves/ from backup (modded Saves/ is parked, never deleted)."
        Write-Host "      backup : $($m.saveIsolation.backupPath)"
        Write-Host "      target : $($m.saveIsolation.originalPath)"
    }
    if ($m -and $m.steamProxy) {
        $n++
        Write-Host "  $n. Restore real steam_api64.dll from backup."
        Write-Host "      backup : $($m.steamProxy.realDllBackupPath)"
        Write-Host "      target : $($m.steamProxy.realDllPath)"
    }
    $n++
    Write-Host "  $n. Remove firewall rule '$RuleName' (LAST - network stays blocked until files are vanilla)."
    if ($haveManifest) { $n++; Write-Host "  $n. Remove manifest." }
    Write-Host ""
    Write-Host "After uninstall, the game is fully online again. Any submissions"
    Write-Host "queued inside Steam's client (from before safe-base was installed)"
    Write-Host "may still sync on next launch."
    $reply = Read-Host "Type YES to continue"
    if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
}

# ---- STEP 1: restore Saves/ (park modded, never delete) ----
if ($m -and $m.saveIsolation) {
    Write-Host "[M3] Restoring Saves/ (parking any modded data)..." -ForegroundColor Cyan
    try {
        $r = Restore-SaveIsolation `
            -SavePath          $m.saveIsolation.originalPath `
            -BackupPath        $m.saveIsolation.backupPath `
            -ExpectedBackupPath $m.saveIsolation.backupPath
        if ($r.restored) {
            Write-Host "       original Saves/ restored to $($m.saveIsolation.originalPath)"
        } else {
            Write-Warning "No backup at $($m.saveIsolation.backupPath) to restore from. Placeholder state left as-is."
        }
    } catch {
        Write-Error "M3 restore failed: $($_.Exception.Message)"
        Write-Host "Backing off before touching other layers. Files are unchanged; investigate and retry."
        if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
        exit 7
    }
} else {
    Write-Host "[M3] No saveIsolation entry in manifest; nothing to restore." -ForegroundColor Yellow
}

# ---- STEP 2: restore real steam_api64.dll ----
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

# ---- STEP 3: remove firewall rule (LAST, so file state is vanilla first) ----
$state = Get-FirewallState
if ($state.Rule) {
    Write-Host "[M1] Removing firewall rule..." -ForegroundColor Cyan
    Remove-NetFirewallRule -Name $RuleName
    Write-Host "       removed: $RuleName"
} else {
    Write-Host "[M1] Rule '$RuleName' was not present (nothing to remove)." -ForegroundColor Yellow
}

# ---- STEP 4: remove manifest ----
if ($haveManifest) {
    Remove-Item -LiteralPath $ManifestPath -Force
    Write-Host "       removed: $ManifestPath"
}

Write-Host ""
Write-Host "OK - safe-base uninstalled. Game is back to online behaviour." -ForegroundColor Green
if ($m -and $m.saveIsolation) {
    Write-Host "Saves/ folder: $($m.saveIsolation.originalPath) should now contain"
    Write-Host "your pre-install saves. Any modded Saves/ from this session is"
    Write-Host "parked under: $BackupDir\saves-modded-<ts>\\"
    Write-Host ""
}
Write-Host "If you had any content mods applied, further play WILL count toward"
Write-Host "leaderboards again. Verify with safe-base\status.ps1."

if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
