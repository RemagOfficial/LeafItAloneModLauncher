# status.ps1 — read-only report of safe-base's state (firewall rule + steam_api64.dll
# proxy + manifest + registered mods). No admin required.

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

# --- verdict ---
Write-Host ""
$m1ok = [bool]($state.Rule -and $state.Rule.Enabled -eq 'True' -and $state.FwServiceStat -eq 'Running')
$m2ok = [bool]($m -and $m.steamProxy -and (Classify-SteamDll -Path $m.steamProxy.realDllPath -ProxySha $m.steamProxy.proxySha256) -eq 'proxy')
if (-not $m) {
    Write-Host "VERDICT: not installed. Content mods that call Assert-SafeBaseActive will refuse to apply." -ForegroundColor Yellow
} elseif ($m1ok -and $m2ok) {
    Write-Host "VERDICT: safe-base M1 + M2 active. Game's outbound TCP+UDP blocked; SteamAPI_Init will fail. Steamworks-based uploads (leaderboards, stats, achievements, cloud saves) cannot happen from the game's process." -ForegroundColor Green
} elseif ($m1ok) {
    Write-Host "VERDICT: partial. M1 firewall rule is fine but the Steam proxy is not in place. Content mods that assume M2 will refuse; run install.ps1 again after fixing whatever broke M2 (currently: see [M2] above)." -ForegroundColor Yellow
} else {
    Write-Host "VERDICT: M1 firewall rule missing or service stopped. Re-run install.ps1 or fix services.msc." -ForegroundColor Yellow
}
