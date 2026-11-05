# uninstall.ps1 — reverses safe-base:
#   * Refuses if Leaf it Alone is running (never mutate live files).
#   * Restores the version.dll overlay FIRST (removes safe-base's version.dll +
#     LeafOverlay.ini; puts back any pre-existing pair that install backed up).
#   * Then restores the game's real Saves/ folder (the modded Saves/ is
#     PARKED under %LOCALAPPDATA%\LeafItAloneSafe\backup\saves-modded-<ts>\,
#     never deleted, so nothing is lost).
#   * Then restores the real steam_api64.dll from backup.
#   * Finally removes the firewall rule LAST, so the network stays blocked
#     until file state is fully vanilla.
#   * Removes the deployed base.psm1 SDK (M5) and the manifest.
#
# Refuses if any content mods are still registered in mods.json (the M5
# registry). Revert them first, or run uninstall-all.ps1.
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
        Write-Host "  - remove safe-base's version.dll + LeafOverlay.ini from the"
        Write-Host "    game folder (restoring any prior pair that install backed"
        Write-Host "    up under $BackupDir\overlay\)"
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
    Write-Error "Leaf it Alone is running (PID(s): $($running.Id -join ', ')). Close the game before uninstalling safe-base. Modded saves may still be in use and version.dll is loaded by the process."
    if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
    exit 6
}

# ---- content-mod guard (reads the M5 registry at mods.json) ----
$regMods = @(Get-ContentModRegistry)
if ($regMods.Count -gt 0) {
    $list = ($regMods | ForEach-Object { "- $($_.name) v$($_.version)" }) -join "`n"
    Write-Error "Content mods are still registered with safe-base:`n$list`n`nRevert them first (each mod's revert.ps1), or run uninstall-all.ps1 which reverts every registered mod and then uninstalls the base."
    if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
    exit 5
}

if (-not $Yes) {
    Write-Host ""
    Write-Host "Removing safe-base."
    $n = 0
    if ($m -and $m.versionOverlay) {
        $n++
        Write-Host "  $n. Restore pre-existing version.dll + LeafOverlay.ini (if install backed any up), otherwise remove ours."
        Write-Host "      target : $($m.versionOverlay.versionDllPath)"
        Write-Host "               $($m.versionOverlay.overlayIniPath)"
    }
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
    if ($haveManifest) { $n++; Write-Host "  $n. Remove the deployed base.psm1 SDK and the manifest." }
    Write-Host ""
    Write-Host "After uninstall, the game is fully online again. Any submissions"
    Write-Host "queued inside Steam's client (from before safe-base was installed)"
    Write-Host "may still sync on next launch."
    $reply = Read-Host "Type YES to continue"
    if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
}

# ---- STEP 1: restore version.dll + LeafOverlay.ini (M4) ----
if ($m -and $m.versionOverlay) {
    Write-Host "[M4] Restoring overlay slot (version.dll + LeafOverlay.ini)..." -ForegroundColor Cyan
    try {
        $r = Restore-VersionOverlay `
            -VersionDllPath        $m.versionOverlay.versionDllPath `
            -OverlayIniPath        $m.versionOverlay.overlayIniPath `
            -ExpectedInstalledSha  $m.versionOverlay.sourceDllSha256 `
            -PriorVersionBackup    $m.versionOverlay.priorVersionBackup `
            -PriorIniBackup        $m.versionOverlay.priorIniBackup
        if ($r.versionRestored) {
            Write-Host "       restored the pre-existing version.dll install backed up at $($m.versionOverlay.priorVersionBackup)"
        } elseif ($r.parked) {
            Write-Warning "current version.dll was not ours; parked at $($r.parked). Prior backup remains at $($m.versionOverlay.priorVersionBackup)."
        } else {
            Write-Host "       removed our version.dll (no prior install existed)"
        }
        if ($r.iniRestored) {
            Write-Host "       restored the pre-existing LeafOverlay.ini"
        } else {
            Write-Host "       removed our LeafOverlay.ini"
        }
    } catch {
        Write-Error "M4 restore failed: $($_.Exception.Message)"
        Write-Host "Backing off before touching other layers. Files are unchanged; investigate and retry."
        if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
        exit 8
    }
} else {
    Write-Host "[M4] No versionOverlay entry in manifest; nothing to restore." -ForegroundColor Yellow
}

# ---- STEP 2: restore Saves/ (park modded, never delete) ----
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

# ---- STEP 3: restore real steam_api64.dll ----
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

# ---- STEP 4: remove firewall rule (LAST, so file state is vanilla first) ----
$state = Get-FirewallState
if ($state.Rule) {
    Write-Host "[M1] Removing firewall rule..." -ForegroundColor Cyan
    Remove-NetFirewallRule -Name $RuleName
    Write-Host "       removed: $RuleName"
} else {
    Write-Host "[M1] Rule '$RuleName' was not present (nothing to remove)." -ForegroundColor Yellow
}

# ---- STEP 5: remove the deployed SDK + manifest ----
if (Restore-BaseSdk -Dest $SdkDeployedPath) {
    Write-Host "[M5] Removed deployed SDK: $SdkDeployedPath" -ForegroundColor Cyan
}
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
