# status.ps1 — read-only report of safe-base's state. No admin required.
# Prints: manifest info, the firewall rule (present / enabled / action / profile /
# program), the Windows Firewall service status, and any registered content mods.

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"

Write-Host "safe-base status" -ForegroundColor Cyan
Write-Host "================================================"

# --- manifest ---
if (Test-Path -LiteralPath $ManifestPath) {
    $m = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    Write-Host "Manifest         : $ManifestPath"
    Write-Host "  installedAt    : $($m.installedAt)"
    Write-Host "  milestone      : $($m.milestone)"
    Write-Host "  gameExe        : $($m.gameExe)"
    if ($m.mods -and @($m.mods).Count -gt 0) {
        Write-Host "  registered mods:"
        foreach ($mod in @($m.mods)) { Write-Host "    - $($mod.name)" }
    } else {
        Write-Host "  registered mods: (none yet)"
    }
} else {
    Write-Host "Manifest         : (absent - safe-base not installed via install.ps1)"
}

# --- live firewall state ---
$state = Get-FirewallState
Write-Host ""
Write-Host "Firewall rule '$RuleName':"
if ($state.Rule) {
    Write-Host "  present        : yes" -ForegroundColor Green
    Write-Host "  enabled        : $($state.Rule.Enabled)"
    Write-Host "  action         : $($state.Rule.Action)"
    Write-Host "  direction      : $($state.Rule.Direction)"
    Write-Host "  profile        : $($state.Rule.Profile)"
    Write-Host "  program        : $($state.Program)"
    if ($m -and $m.gameExe -and $state.Program) {
        # Windows Firewall normalises to a form that may contain %SYSTEMROOT% or quoted paths.
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

# --- Windows Firewall service ---
Write-Host ""
Write-Host "Windows Firewall service (MpsSvc): $($state.FwServiceStat)"
if ($state.FwServiceStat -ne 'Running') {
    Write-Warning "Firewall service is not running. Rules exist but nothing is enforcing them."
    Write-Host "  To fix: services.msc -> Windows Defender Firewall -> Start"
}

# --- verdict ---
Write-Host ""
if ($state.Rule -and $state.Rule.Enabled -eq 'True' -and $state.FwServiceStat -eq 'Running') {
    Write-Host "VERDICT: safe-base M1 active. Game's outbound TCP+UDP is blocked." -ForegroundColor Green
} elseif ($state.Rule) {
    Write-Host "VERDICT: rule present but something's off. See fields above." -ForegroundColor Yellow
} else {
    Write-Host "VERDICT: not installed. Content mods that call Assert-SafeBaseActive will refuse to apply." -ForegroundColor Yellow
}
