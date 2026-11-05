# apply.ps1 - bag-capacity content mod (M6). Doubles the four Leaf it Alone
# bag capacities (50/200/500/800 -> 100/400/1000/1600) by patching the
# coreassets metadata bundle with UnityPy, going through the safe-base SDK so
# the change is gated on the safety framework, backed up, and reversible.
#
#   1. Import the deployed SDK and refuse if the game is running.
#   2. Assert-SafeBaseActive  - safe-base must be installed.
#   3. Assert-SafePreconditions - firewall + Steam proxy + save isolation +
#      banner all verified healthy RIGHT NOW (so we never patch an unsafe base).
#   4. Locate the target bundle from the manifest's gameExe (override -BundlePath).
#   5. Run patch_bag_capacity.py into a staging dir. The patcher refuses unless
#      the bundle holds the vanilla values, and self-verifies its output.
#   6. Backup-Original the live bundle, overwrite it with the staged one, then
#      re-read the LIVE bundle to confirm 2x, then Register-Mod.
#
# Anything failing at 5 leaves the game folder untouched. A failure at 6+ aborts
# before registering, and the pristine backup + registry are what revert uses.

[CmdletBinding()]
param(
    [double]$Scale = 2.0,
    [string]$BundlePath,                       # override auto-detected bundle
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

# 2. resolve the target bundle.
$modName    = 'bag-capacity'
$modVersion = '1.0.0'
$bundleRel  = 'Leaf it Alone_Data\StreamingAssets\aa\StandaloneWindows64\coreassets_assets_meta_ebdcc9c18e1c4d94460e79b368e85abe.bundle'
if (-not $BundlePath) {
    if (-not $m.gameExe) { throw "manifest has no gameExe to locate the game folder; pass -BundlePath." }
    $gameFolder = Split-Path -Parent $m.gameExe
    $BundlePath = Join-Path $gameFolder $bundleRel
}
if (-not (Test-Path -LiteralPath $BundlePath)) {
    Write-Error "target bundle not found at:`n  $BundlePath`nGame may have updated (bundle hash changed) or is installed elsewhere. Pass -BundlePath."
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
Write-Host "  scale    : $Scale  (50->100, 200->400, 500->1000, 800->1600)"
Write-Host "  safety   : M1 + M2 + M3 + M4 verified via Assert-SafePreconditions"
Write-Host ""
if (-not $Yes) {
    $reply = Read-Host "Type YES to patch this bundle (anything else aborts)"
    if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
}

# 4. stage the patch. The patcher validates the vanilla input and verifies its
# own output; a non-zero exit means the live bundle was never touched.
$patcher = Join-Path $PSScriptRoot 'patch_bag_capacity.py'
if (-not (Test-Path -LiteralPath $patcher)) { throw "patcher missing at $patcher" }
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("leaf-bagcap-" + [guid]::NewGuid().ToString('N'))
New-Item -Path $staging -ItemType Directory -Force | Out-Null
try {
    Write-Host "[patch] running UnityPy patcher into staging..." -ForegroundColor Cyan
    $patchOut = & $Python $patcher --in $BundlePath --out-dir $staging --scale $Scale
    $code = $LASTEXITCODE
    Write-Host $patchOut
    if ($code -ne 0) { throw "patcher exited $code; live bundle untouched." }
    $stagedBundle = Join-Path $staging ([System.IO.Path]::GetFileName($BundlePath))
    if (-not (Test-Path -LiteralPath $stagedBundle)) {
        # fall back to any produced .bundle
        $any = Get-ChildItem -LiteralPath $staging -Filter '*.bundle' -File | Select-Object -First 1
        if (-not $any) { throw "patcher reported success but produced no staged .bundle" }
        $stagedBundle = $any.FullName
    }

    # 5. back up the LIVE bundle (pristine) BEFORE overwriting.
    Write-Host "[backup] snapshotting the live bundle via the SDK..." -ForegroundColor Cyan
    $rec = Backup-Original -ModName $modName -TargetPath $BundlePath

    # 6. overwrite the live bundle with the staged (verified) one.
    Write-Host "[apply] replacing the live bundle..." -ForegroundColor Cyan
    Copy-Item -LiteralPath $stagedBundle -Destination $BundlePath -Force

    # 7. re-read the LIVE bundle to prove the applied bytes are correct.
    Write-Host "[verify] re-reading the live bundle..." -ForegroundColor Cyan
    $live = & $Python $patcher --in $BundlePath --verify-only
    $liveCode = $LASTEXITCODE
    Write-Host $live
    if ($liveCode -ne 0) {
        Write-Warning "Verification of the live bundle failed; restoring the pristine backup so the game is not left half-patched."
        Move-Item -LiteralPath $rec.backupPath -Destination $BundlePath -Force
        throw "post-apply verify failed (exit $liveCode); rolled back."
    }

    # 8. register so safe-base can reverse it.
    Register-Mod -Name $modName -Version $modVersion -Files @($rec) `
        -RevertScript (Join-Path $PSScriptRoot 'revert.ps1') `
        -Notes "bag capacities x$Scale" | Out-Null
} finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "OK - bag-capacity applied. Small/Medium/Big/Giant bags are now x$Scale." -ForegroundColor Green
Write-Host "  backup : $($rec.backupPath)"
Write-Host "  revert : content-mods\bag-capacity\revert.ps1  (or safe-base\uninstall-all.ps1)"
Write-Host "Launch the game and check a bag's capacity in the shop/inventory UI."
Write-Host "If the game fails to load the bundle, run revert.ps1 (see README notes on"
Write-Host "the Addressables catalog / Steam 'verify integrity' restoring the original)."
