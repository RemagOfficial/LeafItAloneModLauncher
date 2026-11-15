# apply.ps1 - larger-leaves content mod. Makes every drawn leaf bigger by
# rewriting the one baked float that sets the leaf render size inside the leaf
# shader (Unlit/LeafShader) in the shaders_assets_all bundle, through the
# safe-base SDK so the change is gated on the safety framework, backed up and
# reversible.
#
# WHY THIS IS NOT A DATA MOD: the leaf's on-screen size is not read from any
# asset - the render shader normalises each leaf quad corner to a fixed length
# (div r0.w, l(0.141420), r0.w), so scaling meshes or leafWidths does nothing.
# The only lever is that baked DXBC immediate. patch_leaf_size.py multiplies it
# by the config multiplier AND recomputes each DXBC container's integrity
# digest (the 16 bytes at container+4 are validated when Unity parses the
# shader; leaving them stale makes D3D reject the vertex shader and the leaves
# simply stop drawing).
#
# The size comes from the canonical config
# %LOCALAPPDATA%\LeafItAloneSafe\larger-leaves.json (a single multiplier),
# seeded from the repo larger-leaves.json template on first run - the launcher
# writes this same file. -Scale on the command line overrides it for one run.
# Re-running apply.ps1 after editing the config reverts to vanilla first, then
# re-patches from the pristine backups, so tweaking is just: edit JSON (or set
# it in the launcher), then relaunch.
#
# This game loads bundles through Unity Addressables, which CRC-verifies every
# bundle against catalog.bin and silently hangs on a mismatch. So the mod also
# nulls THIS bundle's catalog CRC (0 = "skip the check") - see
# patch_catalog_null.py, which locates the record from the bundle's own
# filename/GUID/size on disk, so a game update that rehashes the CRC cannot
# break it (nothing is hardcoded).
#
#   1. Import the deployed SDK and refuse if the game is running.
#   2. Assert-SafeBaseActive / Assert-SafePreconditions (never patch unsafe).
#   3. Locate the shaders bundle + the Addressables catalog.bin.
#   4. Resolve the multiplier (CLI override > config file).
#   5. If already applied, Revert-Mod to a clean vanilla baseline first.
#   6. Run patch_leaf_size.py into staging with --size. The patcher refuses
#      unless the shader holds the vanilla constant and self-verifies.
#   7. Backup-Original the live bundle AND catalog.bin, null the catalog CRC,
#      overwrite the bundle, re-read the LIVE bundle to confirm, Register-Mod
#      both files. Any failure after the backups restores BOTH originals.

[CmdletBinding()]
param(
    [string]$Config,                          # canonical = %LOCALAPPDATA%\LeafItAloneSafe\larger-leaves.json (seeded from the repo template); pass to override
    [double]$Scale = 0,                       # >0 overrides the config multiplier
    [string]$BundlePath,                      # override auto-detected bundle
    [string]$CatalogPath,                     # override auto-detected catalog.bin
    [string]$Python = 'C:\Python312\python.exe', # pinned interpreter (3.14 breaks UnityPy)
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
$modName    = 'larger-leaves'
$modVersion = '1.0.0'
$gameFolder = if ($BundlePath) { $null } elseif ($m.gameExe) { Split-Path -Parent $m.gameExe } else { $null }
if (-not $BundlePath) {
    if (-not $gameFolder) { throw "manifest has no gameExe to locate the game folder; pass -BundlePath." }
    # GLOB by stable prefix: a game update can rehash the <guid> in the bundle
    # filename, so never pin the exact name. When an update leaves BOTH the old
    # and the new bundle on disk, the catalog stores the live one as a PATH entry
    # ("\...\<name>.bundle") - prefer those, then newest mtime.
    $aaDirLive = Join-Path $gameFolder 'Leaf it Alone_Data\StreamingAssets\aa\StandaloneWindows64'
    $catFile = Join-Path (Split-Path -Parent $aaDirLive) 'catalog.bin'
    $catalogText = $null
    if (Test-Path -LiteralPath $catFile) {
        $catalogText = [System.Text.Encoding]::GetEncoding(28591).GetString([System.IO.File]::ReadAllBytes($catFile))
    }
    $hits = @(Get-ChildItem -LiteralPath $aaDirLive -Filter 'shaders_assets_all_*.bundle' -File -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending)
    if ($hits.Count -gt 1 -and $catalogText) {
        $ref = @($hits | Where-Object { $catalogText.Contains('\' + $_.Name) })
        if ($ref.Count -ge 1) { $hits = $ref }
    }
    if ($hits.Count -eq 0) {
        Write-Error "no shaders_assets_all_*.bundle found in:`n  $aaDirLive`nGame may have updated or is installed elsewhere. Pass -BundlePath."
        exit 4
    }
    if ($hits.Count -gt 1) { Write-Warning "$($hits.Count) shader bundles on disk (an update can leave the old one); using the one the catalog references / newest: $($hits[0].Name)" }
    $BundlePath = $hits[0].FullName
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

# 3. resolve the target multiplier. Precedence: -Scale > the config file.
if ($Scale -gt 0) {
    $mult = $Scale
    $sizeSource = "command line -Scale $Scale"
}
else {
    # No explicit -Scale: read the config. The canonical file is
    # %LOCALAPPDATA%\LeafItAloneSafe\larger-leaves.json (the same one the
    # launcher writes); it is seeded from the repo template on first run so there
    # is exactly one place to edit. Pass -Config to point somewhere else.
    if (-not $Config) {
        $liveCfg = Join-Path $env:LOCALAPPDATA 'LeafItAloneSafe\larger-leaves.json'
        $repoCfg = Join-Path $PSScriptRoot 'larger-leaves.json'
        if (-not (Test-Path -LiteralPath $liveCfg)) {
            New-Item -Path (Split-Path -Parent $liveCfg) -ItemType Directory -Force | Out-Null
            if (Test-Path -LiteralPath $repoCfg) {
                $seed = Get-Content -LiteralPath $repoCfg -Raw | ConvertFrom-Json
            } else {
                $seed = [pscustomobject]@{ multiplier = 2.0 }
            }
            $seed | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $liveCfg -Encoding UTF8
            Write-Host "seeded the live config at $liveCfg" -ForegroundColor DarkGray
        }
        $Config = $liveCfg
    }
    if (-not (Test-Path -LiteralPath $Config)) {
        Write-Error "no config at $Config and no -Scale given. Edit larger-leaves.json, or pass -Scale <multiplier>."
        exit 7
    }
    $cfg = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
    $mult = if ($cfg.multiplier) { [double]$cfg.multiplier } else { 2.0 }
    $sizeSource = "config $Config (multiplier x$mult)"
}
if ($mult -le 0) { Write-Error "multiplier must be > 0 (got $mult)."; exit 7 }

# 4. if it's already applied, revert to vanilla first, then re-patch from the
# pristine backups. This makes "edit the config -> run apply again" a clean loop
# instead of refusing to stack.
$existing = @(Get-SafeBaseModRegistry | Where-Object { $_.name -eq $modName })
if ($existing.Count -gt 0) {
    Write-Host "'$modName' is already applied - reverting to vanilla first, then re-applying." -ForegroundColor Yellow
    Revert-Mod -Name $modName | Out-Null
}

Write-Host ""
Write-Host "larger-leaves apply" -ForegroundColor Cyan
Write-Host "  bundle   : $BundlePath"
Write-Host "  catalog  : $CatalogPath  (nulls this bundle's CRC record so Addressables loads the repack)"
Write-Host "  size     : x$mult  (leaf render radius 0.141420 -> $([Math]::Round(0.141420 * $mult, 6)))"
Write-Host "  source   : $sizeSource"
Write-Host "  safety   : M1 + M2 + M3 + M4 verified via Assert-SafePreconditions"
Write-Host ""
if (-not $Yes) {
    $reply = Read-Host "Type YES to patch this shader bundle + catalog (anything else aborts)"
    if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
}

# 5. stage the patch. The patcher validates the vanilla input, recomputes every
# DXBC container digest and verifies its own output; a non-zero exit means the
# game folder was never touched.
$patcher    = Join-Path $PSScriptRoot 'patch_leaf_size.py'
$catPatcher = Join-Path $PSScriptRoot 'patch_catalog_null.py'
if (-not (Test-Path -LiteralPath $patcher))    { throw "patcher missing at $patcher" }
if (-not (Test-Path -LiteralPath $catPatcher)) { throw "catalog patcher missing at $catPatcher" }
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("leaf-size-" + [guid]::NewGuid().ToString('N'))
New-Item -Path $staging -ItemType Directory -Force | Out-Null

$recBundle = $null; $recCatalog = $null
try {
    Write-Host "[patch] running shader patcher (with DXBC digest repair) into staging..." -ForegroundColor Cyan
    $patchOut = & $Python $patcher --in $BundlePath --out-dir $staging --size $mult
    $code = $LASTEXITCODE
    Write-Host $patchOut
    if ($code -ne 0) { throw "patcher exited $code; game folder untouched." }
    $stagedBundle = Join-Path $staging ([System.IO.Path]::GetFileName($BundlePath))
    if (-not (Test-Path -LiteralPath $stagedBundle)) {
        $any = Get-ChildItem -LiteralPath $staging -Filter '*.bundle' -File | Select-Object -First 1
        if (-not $any) { throw "patcher reported success but produced no staged .bundle" }
        $stagedBundle = $any.FullName
    }

    # 6. back up the LIVE bundle AND catalog (pristine) BEFORE touching either.
    Write-Host "[backup] snapshotting the live bundle + catalog.bin via the SDK..." -ForegroundColor Cyan
    $recBundle  = Backup-Original -ModName $modName -TargetPath $BundlePath
    $recCatalog = Backup-Original -ModName $modName -TargetPath $CatalogPath

    try {
        # 7. null this bundle's CRC in the catalog so Addressables skips the
        #    check. patch_catalog_null locates the record from the bundle's own
        #    filename/GUID/size (no hardcoded CRC), and refuses on ambiguity.
        Write-Host "[catalog] nullifying this bundle's CRC (update-robust, filename/GUID-anchored)..." -ForegroundColor Cyan
        $catOut = & $Python $catPatcher --catalog $CatalogPath --bundle-path $BundlePath
        $catCode = $LASTEXITCODE
        Write-Host $catOut
        if ($catCode -ne 0) { throw "catalog patcher exited $catCode" }

        # 8. overwrite the live bundle with the staged (verified) one.
        Write-Host "[apply] replacing the live bundle..." -ForegroundColor Cyan
        Copy-Item -LiteralPath $stagedBundle -Destination $BundlePath -Force

        # 9. re-read the LIVE bundle to prove the applied bytes are correct.
        Write-Host "[verify] re-reading the live bundle..." -ForegroundColor Cyan
        $live = (& $Python $patcher --in $BundlePath --verify-only --size $mult) -join "`n"
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

    # 10. register both files so safe-base can reverse the whole change.
    Register-Mod -Name $modName -Version $modVersion -Files @($recBundle, $recCatalog) `
        -RevertScript (Join-Path $PSScriptRoot 'revert.ps1') `
        -Notes "leaf render size x$mult (radius 0.141420 -> $([Math]::Round(0.141420 * $mult, 6)), DXBC digests repaired) (+ catalog CRC nullified)" | Out-Null
} finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "OK - larger-leaves applied. Every leaf is now drawn x$mult." -ForegroundColor Green
Write-Host "  backups: $($recBundle.backupPath)"
Write-Host "         and $($recCatalog.backupPath)"
Write-Host "  revert : content-mods\larger-leaves\revert.ps1  (or safe-base\uninstall-all.ps1)"
Write-Host "Launch the game - the leaf carpet on the patio should be visibly bigger."
Write-Host "Note: Steam 'verify integrity of game files' restores both the bundle and"
Write-Host "catalog.bin; just re-run apply.ps1 afterwards (safe-base still guards you)."
