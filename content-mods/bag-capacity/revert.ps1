# revert.ps1 - undo the bag-capacity content mod. Restores the pristine bundle
# snapshot taken by apply.ps1 (parking the modded file aside, never deleting it)
# and removes the mod from the safe-base registry. Idempotent: if the live
# bundle already matches the recorded original it is left alone, and running it
# again is a no-op.
#
# To undo every content mod and then the whole safety framework in one go, use
# safe-base\uninstall-all.ps1 instead.

[CmdletBinding()]
param([switch]$Yes)
$ErrorActionPreference = 'Stop'

$sdk = Join-Path $env:LOCALAPPDATA 'LeafItAloneSafe\base.psm1'
if (-not (Test-Path -LiteralPath $sdk)) {
    Write-Error "safe-base SDK not found at $sdk. Nothing to revert through; if safe-base was already uninstalled the bundle was restored by uninstall-all.ps1."
    exit 2
}
Import-Module $sdk -Force -DisableNameChecking

if (Test-GameRunning) {
    Write-Error "Leaf it Alone is running. Close the game before reverting."
    exit 3
}

$modName = 'bag-capacity'
$registered = @(Get-SafeBaseModRegistry | Where-Object { $_.name -eq $modName })
if ($registered.Count -eq 0) {
    Write-Host "'$modName' is not registered - nothing to revert." -ForegroundColor Yellow
    exit 0
}
if (-not $Yes) {
    $reply = Read-Host "Revert '$modName' (restore the vanilla bag-capacity bundle)? Type YES to continue"
    if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
}

$report = Revert-Mod -Name $modName
Write-Host ""
Write-Host "bag-capacity reverted." -ForegroundColor Green
foreach ($f in $report.files) {
    Write-Host ("  {0,-7} {1}  ({2})" -f $f.action, $f.targetPath, $f.detail)
}
Write-Host ""
Write-Host "Capacities are back to vanilla 50/200/500/800. The modded bundle is parked"
Write-Host "beside the game folder as .modified-<timestamp> if you want to re-apply quickly."
