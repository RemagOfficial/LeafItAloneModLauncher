# uninstall-all.ps1 - revert EVERY registered content mod, then uninstall the
# safe-base framework. This is the one-stop "put everything back" command:
#
#   1. Refuse if 'Leaf it Alone' is running (never mutate live files).
#   2. Read the content-mod registry (%LOCALAPPDATA%\LeafItAloneSafe\mods.json).
#   3. For each registered mod, Revert-Mod: restore its pristine originals
#      (parking any modified file, never deleting), remove files the mod added
#      (parked too), then drop its registry entry.
#   4. Hand off to uninstall.ps1, which restores the overlay slot, Saves/,
#      the real steam_api64.dll, removes the firewall rule LAST, then removes
#      the deployed SDK + manifest.
#
# Self-elevates via UAC, so the mod reverts (which write into the game folder)
# and the base teardown run in one elevated pass. uninstall.ps1 sees an already
# -admin context and does not prompt a second time.

[CmdletBinding()]
param(
    [switch]$Yes,
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"
# -DisableNameChecking: the SDK exports readable but non-approved verbs
# (Backup-Original, Register-Mod, Revert-Mod, ...). That is intentional; keep
# the import quiet.
Import-Module (Join-Path $PSScriptRoot 'base.psm1') -Force -DisableNameChecking

# ---------------------------------------------------------------------------
# Non-elevated: preview + confirm, then relaunch as admin.
# ---------------------------------------------------------------------------
if (-not (Test-IsAdmin)) {
    $mods = @(Get-SafeBaseModRegistry)
    Write-Host ""
    Write-Host "uninstall-all (revert mods, then remove safe-base)" -ForegroundColor Cyan
    Write-Host "====================================================" -ForegroundColor Cyan
    if ($mods.Count -eq 0) {
        Write-Host "Registered content mods: (none)"
        Write-Host "  -> nothing to revert; will just uninstall the safe-base framework."
    } else {
        Write-Host "Registered content mods ($($mods.Count)):" -ForegroundColor Yellow
        foreach ($mod in $mods) {
            $fc = @($mod.files).Count
            Write-Host "  - $($mod.name) v$($mod.version)  ($fc file(s))  applied $($mod.appliedAt)"
        }
        Write-Host ""
        Write-Host "Each mod's files are restored from"
        Write-Host "  $ModBackupRoot\<mod>\\"
        Write-Host "A file that differs from its recorded original is PARKED (never"
        Write-Host "deleted) alongside its backup. Files a mod ADDED are removed from"
        Write-Host "the game and parked. Then the base layers come off."
    }
    Write-Host ""
    Write-Host "Then uninstall.ps1 runs: overlay slot -> Saves/ -> steam_api64.dll"
    Write-Host "-> firewall rule LAST -> SDK + manifest removed."
    Write-Host ""
    if (-not $Yes) {
        $reply = Read-Host "Type YES to revert all mods and uninstall safe-base (anything else aborts)"
        if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
    }
    Write-Host "Requesting administrator rights (UAC prompt)..." -ForegroundColor Yellow
    $q = '"'
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File ${q}$PSCommandPath${q} -Elevated -Yes"
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
# Elevated: revert every mod, then uninstall the base.
# ---------------------------------------------------------------------------
$running = Get-Process -Name 'Leaf it Alone' -ErrorAction SilentlyContinue
if ($running) {
    Write-Error "Leaf it Alone is running (PID(s): $($running.Id -join ', ')). Close the game before reverting mods / uninstalling safe-base."
    if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
    exit 6
}

$mods = @(Get-SafeBaseModRegistry)
if ($mods.Count -eq 0) {
    Write-Host "[mods] No content mods registered - skipping revert." -ForegroundColor Yellow
} else {
    Write-Host "[mods] Reverting $($mods.Count) content mod(s)..." -ForegroundColor Cyan
    foreach ($mod in $mods) {
        $name = "$($mod.name)"
        try {
            $rep = Revert-Mod -Name $name
            $r = @($rep.filesRestored).Count
            $pk = @($rep.filesParked).Count
            $sk = @($rep.filesUnchanged).Count
            Write-Host "  - $name : restored $r, parked $pk, already-original $sk"
            if ($pk -gt 0) {
                Write-Host "    (modified/added files parked under $ModBackupRoot\$name\)" -ForegroundColor DarkYellow
            }
        } catch {
            Write-Error "Failed to revert mod '$name': $($_.Exception.Message)"
            Write-Host "Aborting before uninstalling the base, so the framework stays consistent while a mod is half-reverted." -ForegroundColor Yellow
            Write-Host "Investigate, then re-run uninstall-all.ps1 (it is idempotent: already-reverted files are detected by SHA and skipped)."
            if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
            exit 7
        }
    }
    # Verify the registry drained (Revert-Mod unregisters each on success).
    $left = @(Get-SafeBaseModRegistry)
    if ($left.Count -gt 0) {
        Write-Error "Registry still lists $(@($left | ForEach-Object { $_.name }) -join ', ') after revert. Refusing to uninstall the base with mods still registered."
        if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
        exit 8
    }
    Write-Host "[mods] All registered mods reverted and unregistered." -ForegroundColor Green
}

# ---- hand off to the base uninstall (already admin -> no second UAC) ----
Write-Host ""
Write-Host "[base] Running uninstall.ps1..." -ForegroundColor Cyan
& "$PSScriptRoot\uninstall.ps1" -Yes -Elevated
$code = $LASTEXITCODE
if ($code -ne 0) {
    Write-Warning "uninstall.ps1 exited with code $code."
}
if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
exit $code
