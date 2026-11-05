# uninstall.ps1 — removes the safe-base M1 firewall rule + manifest.
# Self-elevates if not already admin. Refuses to touch anything if safe-base
# isn't currently installed (so it never collides with the base not being there).

[CmdletBinding()]
param(
    [switch]$Yes,
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"

if (-not (Test-IsAdmin)) {
    if (-not $Yes) {
        Write-Host ""
        Write-Host "This will remove:"
        Write-Host "  - firewall rule '$RuleName'"
        Write-Host "  - manifest at $ManifestPath"
        $reply = Read-Host "Type YES to continue (anything else aborts)"
        if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
    }
    $q = '"'
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File ${q}$PSCommandPath${q} -Elevated -Yes"
    $p = Start-Process -FilePath 'powershell.exe' -Verb RunAs -PassThru -ArgumentList $argLine
    $p.WaitForExit()
    exit $p.ExitCode
}

# --- content-mod guard (M5 will make mods register with the base) ---
if (Test-Path -LiteralPath $ManifestPath) {
    try {
        $m = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
        if ($m.mods -and @($m.mods).Count -gt 0) {
            Write-Error @"
Content mods are still registered with safe-base:
  $(@($m.mods | ForEach-Object { '- ' + $_.name }) -join "`n  ")

Revert them first (each mod's revert.ps1), or run uninstall-all.ps1 (M5)
which reverts every registered mod and then uninstalls the base.
"@
            exit 5
        }
    } catch {
        Write-Warning "Manifest is unreadable ($ManifestPath); will still remove the rule + manifest."
    }
}

if (-not $Yes) {
    Write-Host ""
    Write-Host "Removing firewall rule '$RuleName' and the install manifest."
    Write-Host "After this the game's outbound network is not blocked any more -"
    Write-Host "playing on the same save that was used while modded can leak any"
    Write-Host "queued submissions. (Save-isolation with auto-purge is M3.)"
    $reply = Read-Host "Type YES to continue"
    if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
}

$state = Get-FirewallState
if ($state.Rule) {
    Remove-NetFirewallRule -Name $RuleName
    Write-Host "Removed rule: $RuleName"
} else {
    Write-Host "Rule '$RuleName' was not present (nothing to remove)."
}

if (Test-Path -LiteralPath $ManifestPath) {
    Remove-Item -LiteralPath $ManifestPath -Force
    Write-Host "Removed manifest: $ManifestPath"
}

Write-Host ""
Write-Host "OK - safe-base uninstalled. Game's outbound network is no longer blocked." -ForegroundColor Green
Write-Host "If you had content mods applied, edit them into the game's bundles any"
Write-Host "further changes will count towards online leaderboards again."

if ($Elevated) { Read-Host "Press Enter to close" | Out-Null }
