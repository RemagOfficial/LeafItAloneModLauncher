# Sample revert.ps1 - hands the pristine bundle + catalog back from safe-base.
param(
    [string]$BundlePath,
    [string]$CatalogPath,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$modDir      = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot    = (Resolve-Path (Join-Path $modDir '..\..\..')).Path
$safeBaseDir = Join-Path $repoRoot 'safe-base'
if (-not (Test-Path $safeBaseDir)) { $safeBaseDir = Join-Path $repoRoot 'tools' }
Import-Module (Join-Path $safeBaseDir 'base.psm1') -Force

if (-not $Yes) { Write-Error 'This mod must be reverted non-interactively: pass -Yes.' }

Revert-Mod -Id 'sample-bigger-bags'
Write-Host "sample-bigger-bags reverted."
