# Sample apply.ps1 - reference third-party mod, mirrors the shape of the launcher's
# built-in mods. Users install this mod by clicking Apply on the Mods page, at which
# point the launcher shells out to this script with -Yes and (because we set
# requires.python = true in mod.json) -Python <interpreter>.
param(
    [string]$Config,
    [string]$BundlePath,
    [string]$CatalogPath,
    [string]$Python,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$modDir      = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot    = (Resolve-Path (Join-Path $modDir '..\..\..')).Path

# Locate safe-base: published layout ships tools/, dev layout uses safe-base/.
$safeBaseDir = Join-Path $repoRoot 'safe-base'
if (-not (Test-Path $safeBaseDir)) { $safeBaseDir = Join-Path $repoRoot 'tools' }
Import-Module (Join-Path $safeBaseDir 'base.psm1') -Force

if (-not $Yes) { Write-Error 'This mod must be applied non-interactively: pass -Yes.' }

# Default config lives next to the mod; the launcher seeds the live copy under
# %LOCALAPPDATA%\LeafItAloneSafe\ on first enable and hands that path via -Config.
if (-not $Config)      { $Config      = Join-Path $env:LOCALAPPDATA 'LeafItAloneSafe\sample-bigger-bags.json' }
if (-not (Test-Path $Config)) { Copy-Item (Join-Path $modDir 'sample-bigger-bags.default.json') $Config -ErrorAction SilentlyContinue }
if (-not $BundlePath)  { throw 'BundlePath not resolved. The launcher normally passes it.' }
if (-not $CatalogPath) { $CatalogPath = Join-Path (Split-Path -Parent $BundlePath) 'catalog.bin' }

Assert-SafePreconditions

# Stage: work on a copy so a failure never leaves the live bundle half-written.
$staging = Join-Path $env:TEMP ('lia-sample-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
Backup-Original -Paths @($BundlePath, $CatalogPath)

try {
    & $Python (Join-Path $modDir 'patch_sample_bags.py') `
        --in $BundlePath --out $staging --config $Config
    if ($LASTEXITCODE -ne 0) { throw "patcher exited $LASTEXITCODE" }

    & $Python (Join-Path $safeBaseDir 'patch_catalog_null.py') `
        --catalog $CatalogPath --bundle $BundlePath --out $staging
    if ($LASTEXITCODE -ne 0) { throw "catalog patcher exited $LASTEXITCODE" }

    Copy-Item (Join-Path $staging (Split-Path -Leaf $BundlePath)) $BundlePath -Force
    Copy-Item (Join-Path $staging (Split-Path -Leaf $CatalogPath)) $CatalogPath -Force
    Register-Mod -Id 'sample-bigger-bags' -Files @($BundlePath, $CatalogPath) `
        -Notes 'raises bagCapacities.small to the configured value'
}
catch {
    Revert-Mod -Id 'sample-bigger-bags'
    throw
}
finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}

Write-Host "sample-bigger-bags applied."
