# apply.ps1 - bag-capacity content mod (M6). Doubles the four Leaf it Alone
# bag capacities (50/200/500/800 -> 100/400/1000/1600) by patching the
# coreassets metadata bundle with UnityPy, going through the safe-base SDK so
# the change is gated on the safety framework, backed up, and reversible.
#
# This game loads bundles through Unity Addressables, which CRC-verifies every
# bundle against catalog.bin and silently hangs on a mismatch. So the mod also
# nulls THIS bundle's catalog CRC (0 = "skip the check") - see patch_catalog.py.
#
#   1. Import the deployed SDK and refuse if the game is running.
#   2. Assert-SafeBaseActive  - safe-base must be installed.
#   3. Assert-SafePreconditions - firewall + Steam proxy + save isolation +
#      banner all verified healthy RIGHT NOW (so we never patch an unsafe base).
#   4. Locate the target bundle + the Addressables catalog.bin.
#   5. Run patch_bag_capacity.py into a staging dir. The patcher refuses unless
#      the bundle holds the vanilla values, and self-verifies its output.
#   6. Backup-Original the live bundle AND catalog.bin, null the catalog CRC,
#      overwrite the bundle, re-read the LIVE bundle to confirm 2x, Register-Mod
#      both files. Any failure after the backups restores BOTH originals.
#
# Anything failing at 5 leaves the game folder untouched. A failure at 6+ rolls
# both files back before registering, and the pristine backups + registry are
# what revert.ps1 / uninstall-all.ps1 use.

[CmdletBinding()]
param(
    [double]$Scale = 2.0,
    [string]$BundlePath,                       # override auto-detected bundle
    [string]$CatalogPath,                      # override auto-detected catalog.bin
    [string]$VanillaCrc = 'cd9380f1',          # the bundle's CRC in catalog.bin on a stock install
    [string]$Python = 'C:\Python312\python.exe',# pinned interpreter (3.14 breaks UnityPy)
    [switch]$Yes
)
$ErrorActionPreference = 'Stop'

# 1. SDK is deployed by safe-base install.ps1. -DisableNameChecking: the SDK's
# readable verbs (Backup-Original, Register-Mod) are non-approved; ignore it.
$sdk = Join-Path $env:LOCALAPPDATA 'LeafItAloneSafe\base.psm1'
if (-not (Test-Path -LiteralPath $sdk)) {
    Write-Error "safe-base SDK not found at $sdk. Install safe-base first: safe-base\install.ps1"
    exit 2
}
Import-Module $sdk -Force -DisableNameChecking

if (Test-GameRunning) {
    Write-Error "Leaf it Alone is running. Close the game before applying/reverting a content mod."
    exit 3
}
$m = Assert-SafeBaseActive        # throws unless the base is installed
Assert-SafePreconditions         # throws unless every safety layer is healthy now

# 2. resolve the target bundle and the Addressables catalog.
$modName    = 'bag-capacity'
$modVersion = '1.1.0'
$gameFolder = if ($BundlePath) { $null } elseif ($m.gameExe) { Split-Path -Parent $m.gameExe } else { $null }
$bundleRel  = 'Leaf it Alone_Data\StreamingAssets\aa\StandaloneWindows64\coreassets_assets_meta_ebdcc9c18e1c4d94460e79b368e85abe.bundle'
if (-not $BundlePath) {
    if (-not $gameFolder) { throw "manifest has no gameExe to locate the game folder; pass -BundlePath." }
    $BundlePath = Join-Path $gameFolder $bundleRel
}
if (-not $CatalogPath) {
    # catalog.bin sits at <...>/StreamingAssets/aa/catalog.bin, two levels above the bundle
    $aaDir = Split-Path -Parent (Split-Path -Parent $BundlePath)
    $CatalogPath = Join-Path $aaDir 'catalog.bin'
}
if (-not (Test-Path -LiteralPath $BundlePath)) {
    Write-Error "target bundle not found at:`n  $BundlePath`nGame may have updated (bundle hash changed) or is installed elsewhere. Pass -BundlePath."
    exit 4
}
if (-not (Test-Path -LiteralPath $CatalogPath)) {
    Write-Error "Addressables catalog not found at:`n  $CatalogPath`nPass -CatalogPath if the layout differs."
    exit 4
}
if (-not (Test-Path -LiteralPath $Python)) {
    Write-Error "pinned Python not found at $Python. UnityPy needs CPython 3.12 (3.14 segfaults on its lz4/brotli deps). Install it or pass -Python."
    exit 5
}

# 3. refuse to stack on an existing application.
$existing = @(Get-SafeBaseModRegistry | Where-Object { $_.name -eq $modName })
if ($existing.Count -gt 0) {
    Write-Error "'$modName' is already registered. Revert first (revert.ps1, or uninstall-all.ps1) before re-applying."
    exit 6
}

Write-Host ""
Write-Host "bag-capacity apply" -ForegroundColor Cyan
Write-Host "  bundle   : $BundlePath"
Write-Host "  catalog  : $CatalogPath  (nulls this bundle's CRC $VanillaCrc so Addressables loads the repack)"
Write-Host "  scale    : $Scale  (50->100, 200->400, 500->1000, 800->1600)"
Write-Host "  safety   : M1 + M2 + M3 + M4 verified via Assert-SafePreconditions"
Write-Host ""
if (-not $Yes) {
    $reply = Read-Host "Type YES to patch this bundle + catalog (anything else aborts)"
    if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
}

# 4. stage the patch. The patcher validates the vanilla input and verifies its
# own output; a non-zero exit means the game folder was never touched.
$patcher    = Join-Path $PSScriptRoot 'patch_bag_capacity.py'
$catPatcher = Join-Path $PSScriptRoot 'patch_catalog.py'
if (-not (Test-Path -LiteralPath $patcher))    { throw "patcher missing at $patcher" }
if (-not (Test-Path -LiteralPath $catPatcher)) { throw "catalog patcher missing at $catPatcher" }
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("leaf-bagcap-" + [guid]::NewGuid().ToString('N'))
New-Item -Path $staging -ItemType Directory -Force | Out-Null

# vanilla (pre-patch) bundle size, captured now, confirms the catalog record
$bundleSize = (Get-Item -LiteralPath $BundlePath).Length
$recBundle = $null; $recCatalog = $null
try {
    Write-Host "[patch] running UnityPy patcher into staging..." -ForegroundColor Cyan
    $patchOut = & $Python $patcher --in $BundlePath --out-dir $staging --scale $Scale
    $code = $LASTEXITCODE
    Write-Host $patchOut
    if ($code -ne 0) { throw "patcher exited $code; game folder untouched." }
    $stagedBundle = Join-Path $staging ([System.IO.Path]::GetFileName($BundlePath))
    if (-not (Test-Path -LiteralPath $stagedBundle)) {
        # fall back to any produced .bundle
        $any = Get-ChildItem -LiteralPath $staging -Filter '*.bundle' -File | Select-Object -First 1
        if (-not $any) { throw "patcher reported success but produced no staged .bundle" }
        $stagedBundle = $any.FullName
    }

    # 5. back up the LIVE bundle AND catalog (pristine) BEFORE touching either.
    Write-Host "[backup] snapshotting the live bundle + catalog.bin via the SDK..." -ForegroundColor Cyan
    $recBundle  = Backup-Original -ModName $modName -TargetPath $BundlePath
    $recCatalog = Backup-Original -ModName $modName -TargetPath $CatalogPath

    try {
        # 6. null this bundle's CRC in the catalog so Addressables skips the
        #    check (patch_catalog refuses on a non-unique/size-mismatched hit).
        Write-Host "[catalog] nullifying this bundle's CRC ($VanillaCrc)..." -ForegroundColor Cyan
        $catOut = & $Python $catPatcher --catalog $CatalogPath --crc $VanillaCrc --expect-size $bundleSize
        $catCode = $LASTEXITCODE
        Write-Host $catOut
        if ($catCode -ne 0) { throw "catalog patcher exited $catCode" }

        # 7. overwrite the live bundle with the staged (verified) one.
        Write-Host "[apply] replacing the live bundle..." -ForegroundColor Cyan
        Copy-Item -LiteralPath $stagedBundle -Destination $BundlePath -Force

        # 8. re-read the LIVE bundle to prove the applied bytes are correct.
        Write-Host "[verify] re-reading the live bundle..." -ForegroundColor Cyan
        $live = (& $Python $patcher --in $BundlePath --verify-only) -join "`n"
        $liveCode = $LASTEXITCODE
        Write-Host $live
        if ($liveCode -ne 0) { throw "post-apply bundle verify exited $liveCode" }
    } catch {
        # any failure after the backups: restore BOTH originals so the game is
        # never left half-patched, then re-throw.
        Write-Warning "Apply failed ($_); restoring the pristine bundle + catalog backups."
        Copy-Item -LiteralPath $recBundle.backupPath  -Destination $BundlePath  -Force
        Copy-Item -LiteralPath $recCatalog.backupPath -Destination $CatalogPath -Force
        throw
    }

    # 9. register both files so safe-base can reverse the whole change.
    Register-Mod -Name $modName -Version $modVersion -Files @($recBundle, $recCatalog) `
        -RevertScript (Join-Path $PSScriptRoot 'revert.ps1') `
        -Notes "bag capacities x$Scale (+ catalog CRC nullified)" | Out-Null
} finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "OK - bag-capacity applied. Small/Medium/Big/Giant bags are now x$Scale." -ForegroundColor Green
Write-Host "  backups: $($recBundle.backupPath)"
Write-Host "         and $($recCatalog.backupPath)"
Write-Host "  revert : content-mods\bag-capacity\revert.ps1  (or safe-base\uninstall-all.ps1)"
Write-Host "Launch the game and check a bag's capacity in the shop/inventory UI."
Write-Host "Note: Steam 'verify integrity of game files' restores both the bundle and"
Write-Host "catalog.bin; just re-run apply.ps1 afterwards (safe-base still guards you)."

