# status.ps1 — read-only report of safe-base's state (firewall rule + steam_api64.dll
# proxy + save isolation + version.dll overlay). No admin required.

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"

Write-Host "safe-base status" -ForegroundColor Cyan
Write-Host "================================================"

# --- manifest ---
$m = $null
if (Test-Path -LiteralPath $ManifestPath) {
    try { $m = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json }
    catch { Write-Warning "Manifest unreadable: $_" }
    Write-Host "Manifest         : $ManifestPath"
    if ($m) {
        Write-Host "  installedAt    : $($m.installedAt)"
        Write-Host "  milestone      : $($m.milestone)"
        Write-Host "  gameExe        : $($m.gameExe)"
        if ($m.mods -and @($m.mods).Count -gt 0) {
            Write-Host "  registered mods:"
            foreach ($mod in @($m.mods)) { Write-Host "    - $($mod.name)" }
        } else {
            Write-Host "  registered mods: (none yet)"
        }
    }
} else {
    Write-Host "Manifest         : (absent - safe-base not installed via install.ps1)"
}

# --- live firewall state (M1) ---
$state = Get-FirewallState
Write-Host ""
Write-Host "[M1] Firewall rule '$RuleName':"
if ($state.Rule) {
    Write-Host "  present        : yes" -ForegroundColor Green
    Write-Host "  enabled        : $($state.Rule.Enabled)"
    Write-Host "  action         : $($state.Rule.Action)"
    Write-Host "  direction      : $($state.Rule.Direction)"
    Write-Host "  profile        : $($state.Rule.Profile)"
    Write-Host "  program        : $($state.Program)"
    if ($m -and $m.gameExe -and $state.Program) {
        $lhs = $state.Program.ToLowerInvariant().Trim('"')
        $rhs = $m.gameExe.ToLowerInvariant()
        if ($lhs -ne $rhs) {
            Write-Host "  !! rule targets a different exe than the manifest recorded" -ForegroundColor Yellow
            Write-Host "     manifest: $($m.gameExe)"
            Write-Host "     rule    : $($state.Program)"
        }
    }
} else {
    Write-Host "  present        : NO" -ForegroundColor Yellow
    Write-Host "  (run install.ps1 to add the block rule)"
}
Write-Host "  MpsSvc         : $($state.FwServiceStat)"
if ($state.FwServiceStat -ne 'Running') {
    Write-Warning "Firewall service is not running. Rules exist but nothing is enforcing them."
    Write-Host "    To fix: services.msc -> Windows Defender Firewall -> Start"
}

# --- steam_api64.dll proxy state (M2) ---
Write-Host ""
Write-Host "[M2] Steam proxy layer:"
if ($m -and $m.steamProxy) {
    $cur = $m.steamProxy.realDllPath
    $kind = Classify-SteamDll -Path $cur -ProxySha $m.steamProxy.proxySha256
    $sz = if (Test-Path -LiteralPath $cur) { (Get-Item -LiteralPath $cur).Length } else { 0 }
    Write-Host "  path           : $cur"
    switch ($kind) {
        'proxy'   { Write-Host "  current DLL    : PROXY (safe-base active, $sz bytes)" -ForegroundColor Green }
        'real'    { Write-Host "  current DLL    : REAL Steam DLL ($sz bytes) - the proxy is NOT in place" -ForegroundColor Yellow }
        'absent'  { Write-Host "  current DLL    : ABSENT at that path - something's wrong" -ForegroundColor Red }
        'unknown' { Write-Host "  current DLL    : UNKNOWN ($sz bytes, hash doesn't match either proxy or backup)" -ForegroundColor Yellow }
    }
    Write-Host "  proxy sha256   : $($m.steamProxy.proxySha256)"
    Write-Host "  real   sha256  : $($m.steamProxy.realDllSha256)  (from backup)"
    Write-Host "  backup         : $($m.steamProxy.realDllBackupPath)"
    if (Test-Path -LiteralPath $m.steamProxy.realDllBackupPath) {
        Write-Host "  backup present : yes" -ForegroundColor Green
    } else {
        Write-Host "  backup present : MISSING - uninstall will not be able to restore" -ForegroundColor Red
    }
} elseif ($m) {
    Write-Host "  manifest has no steamProxy entry (installed with -SkipSteam? or M1-only)" -ForegroundColor Yellow
} else {
    Write-Host "  not installed"
}

# --- save isolation state (M3) ---
Write-Host ""
Write-Host "[M3] Save isolation layer:"
if ($m -and $m.saveIsolation) {
    $orig  = $m.saveIsolation.originalPath
    $bk    = $m.saveIsolation.backupPath
    $empty = Join-Path $orig '.safebase-empty'
    Write-Host "  source         : $orig"
    if (Test-Path -LiteralPath $orig) {
        $kids = Get-ChildItem -LiteralPath $orig -Force -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -ne $empty }
        if ($kids.Count -eq 0) {
            Write-Host "  current state  : ISOLATED (empty placeholder present)" -ForegroundColor Green
        } else {
            Write-Host "  current state  : ACTIVE ($($kids.Count) file(s) in Saves/) - game has been played while modded" -ForegroundColor DarkGreen
        }
    } else {
        Write-Host "  current state  : MISSING at $orig - install never ran, or user deleted it" -ForegroundColor Red
    }
    Write-Host "  placeholder    : $(if (Test-Path -LiteralPath $empty) { 'marker present' } else { 'marker absent' })"
    Write-Host "  backup         : $bk"
    if ($bk -and (Test-Path -LiteralPath $bk)) {
        $bkKids = @(Get-ChildItem -LiteralPath $bk -Force -Recurse -File -ErrorAction SilentlyContinue)
        $bkSum = [int64]0
        foreach ($k in $bkKids) { $bkSum += $k.Length }
        Write-Host "  backup present : yes ($($bkKids.Count) file(s), $bkSum bytes)" -ForegroundColor Green
    } elseif ($bk) {
        Write-Host "  backup present : MISSING - uninstall will not be able to restore originals" -ForegroundColor Red
    } else {
        Write-Host "  backup present : n/a (AllowMissing path - no prior Saves/ to back up)"
    }
    Write-Host "  pre-move info  : $($m.saveIsolation.preMoveCount) file(s), $($m.saveIsolation.preMoveBytes) bytes at install time"
    if ($m.saveIsolation.steamCloudVdf) {
        Write-Host "  Steam Cloud    : marker was present at install (moved to backup with Saves/)"
        if (Test-SteamCloudMarker -SavePath $orig) {
            Write-Host "                 !! a marker has REAPPEARED in the current Saves/ - Steam may be actively syncing" -ForegroundColor Red
            Write-Host "                    Disable Steam Cloud for the game: Steam Library -> right-click Leaf it Alone -> Properties -> Updates"
        } else {
            Write-Host "                 current Saves/ has no marker (fine either way; the isolated folder is a dead drop)" -ForegroundColor DarkGray
        }
    } else {
        Write-Host "  Steam Cloud    : marker absent at install time (no cloud sync configured for this account)"
    }
} elseif ($m) {
    Write-Host "  manifest has no saveIsolation entry (installed with -SkipSaves? or pre-M3)" -ForegroundColor Yellow
} else {
    Write-Host "  not installed"
}

# --- version.dll overlay state (M4) ---
Write-Host ""
Write-Host "[M4] Overlay banner layer (version.dll):"
if ($m -and $m.versionOverlay) {
    $vdll = $m.versionOverlay.versionDllPath
    $vini = $m.versionOverlay.overlayIniPath
    Write-Host "  version.dll    : $vdll"
    if (Test-Path -LiteralPath $vdll) {
        $sz = (Get-Item -LiteralPath $vdll).Length
        $sh = Get-FileSha256 -Path $vdll
        if ($sh -and $sh -eq $m.versionOverlay.sourceDllSha256) {
            Write-Host "                   PRESENT, matches installed SHA (safe-base banner active) ($sz bytes)" -ForegroundColor Green
        } else {
            Write-Host "                   PRESENT, SHA does NOT match install (a different DLL is in place: $sz bytes)" -ForegroundColor Yellow
        }
    } else {
        Write-Host "                   MISSING - the game won't show the OFFLINE banner" -ForegroundColor Red
    }
    Write-Host "  LeafOverlay.ini: $vini"
    if (Test-Path -LiteralPath $vini) {
        $txt = Get-Content -LiteralPath $vini -Raw -ErrorAction SilentlyContinue
        if ($txt -and $txt -match '(?im)^\s*Enabled\s*=\s*true') {
            Write-Host "                   PRESENT with [safebase] Enabled=true" -ForegroundColor Green
        } else {
            Write-Host "                   PRESENT but [safebase] not Enabled (edits? overlay will draw HUD only)" -ForegroundColor Yellow
        }
    } else {
        Write-Host "                   MISSING" -ForegroundColor Red
    }
    $exp = $m.versionOverlay.expectedProxySize
    if ($exp) { Write-Host "  expected size  : steam proxy = $exp bytes (checked by overlay at startup)" }
    if ($m.versionOverlay.priorVersionBackup) {
        if (Test-Path -LiteralPath $m.versionOverlay.priorVersionBackup) {
            Write-Host "  prior backup   : present ($($m.versionOverlay.priorVersionBackup))" -ForegroundColor Green
        } else {
            Write-Host "  prior backup   : MISSING at $($m.versionOverlay.priorVersionBackup)" -ForegroundColor Red
        }
    } else {
        Write-Host "  prior backup   : n/a (no version.dll was in place at install time)"
    }
} elseif ($m) {
    Write-Host "  manifest has no versionOverlay entry (installed with -SkipOverlay? or pre-M4)" -ForegroundColor Yellow
} else {
    Write-Host "  not installed"
}

# --- verdict ---
Write-Host ""
$m1ok = [bool]($state.Rule -and $state.Rule.Enabled -eq 'True' -and $state.FwServiceStat -eq 'Running')
$m2ok = [bool]($m -and $m.steamProxy -and (Classify-SteamDll -Path $m.steamProxy.realDllPath -ProxySha $m.steamProxy.proxySha256) -eq 'proxy')
$m3ok = [bool]($m -and $m.saveIsolation -and $m.saveIsolation.backupPath -and (Test-Path -LiteralPath $m.saveIsolation.backupPath))
$m4ok = [bool]($m -and $m.versionOverlay -and (Test-Path -LiteralPath $m.versionOverlay.versionDllPath) -and (Test-Path -LiteralPath $m.versionOverlay.overlayIniPath))
$allExpected = $m1ok -and $m2ok -and $m3ok -and $m4ok
if (-not $m) {
    Write-Host "VERDICT: not installed. Content mods that call Assert-SafeBaseActive will refuse to apply." -ForegroundColor Yellow
} elseif ($allExpected) {
    Write-Host "VERDICT: safe-base M1 + M2 + M3 + M4 active. Game's outbound TCP+UDP blocked; SteamAPI_Init fails; Saves/ is isolated with the original safely backed up; version.dll banner runs and reports these states in-game." -ForegroundColor Green
} elseif ($m1ok -and $m2ok -and $m3ok) {
    Write-Host "VERDICT: M1 + M2 + M3 active, M4 overlay missing or INI absent. The safety layers work but there is no on-screen banner. Fix: re-run install.ps1 with -SkipOverlay unset, or copy version.dll + LeafOverlay.ini back into the game folder." -ForegroundColor Yellow
} elseif ($m1ok -and $m2ok) {
    Write-Host "VERDICT: safe-base M1 + M2 active, but M3 is not (backup missing or -SkipSaves install). Steamworks and web-service uploads are blocked, but modded play still writes to your real save. Fix: re-run install.ps1 without -SkipSaves, or run uninstall and reinstall." -ForegroundColor Yellow
} elseif ($m1ok) {
    Write-Host "VERDICT: partial. M1 firewall rule is fine but the Steam proxy is not in place. Content mods that assume M2 will refuse; run install.ps1 again after fixing whatever broke M2 (currently: see [M2] above)." -ForegroundColor Yellow
} else {
    Write-Host "VERDICT: M1 firewall rule missing or service stopped. Re-run install.ps1 or fix services.msc." -ForegroundColor Yellow
}
