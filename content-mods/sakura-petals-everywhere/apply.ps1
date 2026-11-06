# apply.ps1 - sakura-petals-everywhere content mod. Makes the sakura map's
# petal leaves appear in the other worlds by swapping each chosen world's
# leaf MATERIAL reference (LeafSettingsSceneProperty.material) from the normal
# "Leaves" material to sakura's "CherryLeaves" material, on the Hometown /
# Castle / Sakura scene-definition assets in the coreassets SCENES bundle
# (coreassets_assets_scenes_5f42bdb....bundle), through the safe-base SDK so
# the change is gated, backed up and reversible. This is NOT a particle/VFX
# change and NOT the petalsort minigame; it is the same material the sakura
# map already uses, so the leaves stay fully functional.
#
# (v1 repointed the LeafArea actors' prefab GUID in the ACTORS bundle instead -
# that turned out not to be the appearance lever and is reverted automatically
# when this apply runs.)
#
# Only the leaf LOOK changes - each world keeps its own leaf counts, scatter
# positions and bounds (they live on the LeafArea itself, not the prefab).
# NOTE: visual leaf SIZE cannot be changed from data files - the shader draws
# leaves from a compute-populated structured buffer (leaves[]) with a hardcoded
# size constant in DXBC bytecode. leafWidths only controls pile PHYSICS.
#
# Which worlds to convert comes from the canonical config
# %LOCALAPPDATA%\LeafItAloneSafe\sakura-petals-everywhere.json (hometown/castle
# booleans), seeded from the repo template on first run - the
# launcher writes this same file. -Hometown / -Castle / -All on the
# command line override it for one run. Re-running apply.ps1 after editing the
# config reverts to vanilla first, then re-patches from the pristine backups.
#
# This game loads bundles through Unity Addressables, which CRC-verifies every
# PRIMARY (addressable-requested) bundle against catalog.bin and silently hangs
# on a mismatch, so the mod nulls the SCENES bundle's catalog CRC (0 = "skip the
# check") via patch_catalog_null.py, which locates the record from the bundle's
# own filename/GUID/size on disk - no hardcoded CRC, so a game update cannot
# break it. The step is idempotent: an already-zeroed record is a no-op. faster-
# bag-tying / bag-capacity patch DIFFERENT bundles (actors / meta), so their CRC
# anchors in the catalog are untouched by this mod.
#
#   1. Import the deployed SDK and refuse if the game is running.
#   2. Assert-SafeBaseActive + Assert-SafePreconditions (safety verified now).
#   3. Locate the target scenes bundle + the Addressables catalog.bin.
#   4. Resolve which worlds to convert (CLI overrides > config file).
#   5. If already applied, Revert-Mod to a clean baseline first.
#   6. Run patch_sakura_petals.py into staging (repacks the scenes bundle).
#      Refuses unless input is pristine and self-verifies output.
#   7. Backup-Original the live scenes bundle AND catalog.bin, null the SCENES
#      bundle's CRC, overwrite the patched bundle, re-read the LIVE bundle to
#      confirm, Register-Mod every touched file. Any failure after the backups
#      restores every original.

[CmdletBinding()]
param(
    [string]$Config,                          # canonical = %LOCALAPPDATA%\LeafItAloneSafe\sakura-petals-everywhere.json (seeded from the repo template); pass to override
    [switch]$Hometown,                        # force-convert Hometown this run (overrides config)
    [switch]$Castle,                          # force-convert Castle this run (overrides config)
    [switch]$All,                             # convert both worlds regardless of config
    [string]$BundlePath,                      # override auto-detected scenes bundle
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
$modName    = 'sakura-petals-everywhere'
$modVersion = '2.4.0'
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
    $hits = @(Get-ChildItem -LiteralPath $aaDirLive -Filter 'coreassets_assets_scenes_*.bundle' -File -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending)
    if ($hits.Count -gt 1 -and $catalogText) {
        $ref = @($hits | Where-Object { $catalogText.Contains('\' + $_.Name) })
        if ($ref.Count -ge 1) { $hits = $ref }
    }
    if ($hits.Count -eq 0) {
        Write-Error "no coreassets_assets_scenes_*.bundle found in:`n  $aaDirLive`nGame may have updated or is installed elsewhere. Pass -BundlePath."
        exit 4
    }
    if ($hits.Count -gt 1) { Write-Warning "$($hits.Count) scenes bundles on disk (an update can leave the old one); using the newest: $($hits[0].Name)" }
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

# 3. resolve which worlds to convert. Precedence:
# explicit -All > -Hometown/-Castle switches > the config file booleans.
$convHometown = $false
$convCastle   = $false
$worldSource  = ''
if ($All) {
    $convHometown = $true; $convCastle = $true
    $worldSource = 'command line -All'
}
elseif ($Hometown -or $Castle) {
    $convHometown = [bool]$Hometown
    $convCastle   = [bool]$Castle
    $worldSource  = 'command line -Hometown/-Castle'
}
if (-not $worldSource) {
    # No CLI switches: read the config. The canonical file is
    # %LOCALAPPDATA%\LeafItAloneSafe\sakura-petals-everywhere.json (the same one
    # the launcher writes); it is seeded from the repo template on first run.
    if (-not $Config) {
        $liveCfg = Join-Path $env:LOCALAPPDATA 'LeafItAloneSafe\sakura-petals-everywhere.json'
        $repoCfg = Join-Path $PSScriptRoot 'sakura-petals-everywhere.json'
        if (-not (Test-Path -LiteralPath $liveCfg)) {
            New-Item -Path (Split-Path -Parent $liveCfg) -ItemType Directory -Force | Out-Null
            if (Test-Path -LiteralPath $repoCfg) {
                $seed = Get-Content -LiteralPath $repoCfg -Raw | ConvertFrom-Json
            } else {
                $seed = [pscustomobject]@{ hometown = $true; castle = $true }
            }
            $seed | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $liveCfg -Encoding UTF8
            Write-Host "seeded the live config at $liveCfg" -ForegroundColor DarkGray
        }
        $Config = $liveCfg
    }
    if (-not (Test-Path -LiteralPath $Config)) {
        Write-Error "no config at $Config and no -All/-Hometown/-Castle given. Edit sakura-petals-everywhere.json, or pass -All / -Hometown / -Castle."
        exit 7
    }
    $cfg = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
    $convHometown = [bool]$cfg.hometown
    $convCastle   = [bool]$cfg.castle
    $worldSource  = "config $Config"
}

if (-not $convHometown -and -not $convCastle) {
    Write-Error "nothing to do: no worlds selected. Turn a world on, or pass -All."
    exit 7
}

# 4. if it's already applied, revert to the prior baseline first, then re-patch
# from the pristine backups. This makes "edit the config -> run apply again" a
# clean loop.
$existing = @(Get-SafeBaseModRegistry | Where-Object { $_.name -eq $modName })
if ($existing.Count -gt 0) {
    Write-Host "'$modName' is already applied - reverting to the prior state first, then re-applying." -ForegroundColor Yellow
    Revert-Mod -Name $modName | Out-Null
}

Write-Host ""
Write-Host "sakura-petals-everywhere apply" -ForegroundColor Cyan
Write-Host "  bundle   : $BundlePath"
Write-Host "  catalog  : $CatalogPath  (nulls the scenes bundle's CRC so Addressables loads the repack)"
Write-Host "  convert  : hometown=$convHometown  castle=$convCastle"
Write-Host "  source   : $worldSource"
Write-Host "  safety   : M1 + M2 + M3 + M4 verified via Assert-SafePreconditions"
Write-Host ""
if (-not $Yes) {
    $reply = Read-Host "Type YES to patch this bundle + catalog (anything else aborts)"
    if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
}

# 5. stage the patches. The patcher validates the vanilla input and verifies its
# own output; a non-zero exit means the game folder was never touched.
$patcher     = Join-Path $PSScriptRoot 'patch_sakura_petals.py'
$catPatcher  = Join-Path $PSScriptRoot 'patch_catalog_null.py'
if (-not (Test-Path -LiteralPath $patcher))     { throw "patcher missing at $patcher" }
if (-not (Test-Path -LiteralPath $catPatcher))  { throw "catalog patcher missing at $catPatcher" }
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("leaf-sakurapetals-" + [guid]::NewGuid().ToString('N'))
New-Item -Path $staging -ItemType Directory -Force | Out-Null

$recBundle = $null; $recCatalog = $null
try {
    Write-Host "[patch] running UnityPy scenes patcher into staging..." -ForegroundColor Cyan
    $patchArgs = @($patcher, '--in', $BundlePath, '--out-dir', $staging)
    if ($convHometown) { $patchArgs += '--hometown' }
    if ($convCastle)   { $patchArgs += '--castle' }
    $patchOut = & $Python @patchArgs
    $code = $LASTEXITCODE
    Write-Host $patchOut
    if ($code -ne 0) { throw "scenes patcher exited $code; game folder untouched." }
    $stagedBundle = Join-Path $staging ([System.IO.Path]::GetFileName($BundlePath))
    if (-not (Test-Path -LiteralPath $stagedBundle)) {
        $any = Get-ChildItem -LiteralPath $staging -Filter '*.bundle' -File | Select-Object -First 1
        if (-not $any) { throw "scenes patcher reported success but produced no staged .bundle" }
        $stagedBundle = $any.FullName
    }

    # 6. back up the LIVE files (pristine) BEFORE touching any of them.
    Write-Host "[backup] snapshotting the live bundle + catalog.bin via the SDK..." -ForegroundColor Cyan
    $recBundle  = Backup-Original -ModName $modName -TargetPath $BundlePath
    $recCatalog = Backup-Original -ModName $modName -TargetPath $CatalogPath

    try {
        # 7. null the SCENES bundle's CRC in the catalog so Addressables skips the
        #    check. patch_catalog_null locates the record from the bundle's own
        #    filename/GUID/size (no hardcoded CRC) and is idempotent, so a re-run
        #    (or a prior co-mod) is a no-op.
        Write-Host "[catalog] nullifying the scenes bundle CRC (update-robust, filename/GUID-anchored)..." -ForegroundColor Cyan
        $catOut = & $Python $catPatcher --catalog $CatalogPath --bundle-path $BundlePath
        $catCode = $LASTEXITCODE
        Write-Host $catOut
        if ($catCode -ne 0) { throw "catalog patcher (scenes) exited $catCode" }

        # 8. overwrite the live bundle with the staged (verified) one.
        Write-Host "[apply] replacing the live bundle..." -ForegroundColor Cyan
        Copy-Item -LiteralPath $stagedBundle -Destination $BundlePath -Force

        # 9. re-read the LIVE bundle to prove the applied bytes are correct.
        Write-Host "[verify] re-reading the live scenes bundle..." -ForegroundColor Cyan
        $live = (& $Python $patcher --in $BundlePath --verify-only) -join "`n"
        $liveCode = $LASTEXITCODE
        Write-Host $live
        if ($liveCode -ne 0) { throw "post-apply scenes verify exited $liveCode" }
    } catch {
        Write-Warning "Apply failed ($_); restoring the pristine bundle + catalog backups."
        Copy-Item -LiteralPath $recBundle.backupPath  -Destination $BundlePath  -Force
        Copy-Item -LiteralPath $recCatalog.backupPath -Destination $CatalogPath -Force
        throw
    }

    $worldNotes = (@(if ($convHometown) { 'hometown' }; if ($convCastle) { 'castle' }) -join '+')
    # 10. register every touched file so safe-base can reverse the whole change.
    $regFiles = @($recBundle, $recCatalog)
    Register-Mod -Name $modName -Version $modVersion -Files $regFiles `
        -RevertScript (Join-Path $PSScriptRoot 'revert.ps1') `
        -Notes "leaf material -> sakura CherryLeaves for: $worldNotes" | Out-Null
} finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "OK - sakura-petals-everywhere applied. Petal leaves now spawn in: $worldNotes." -ForegroundColor Green
Write-Host "  backups: $($recBundle.backupPath)"
Write-Host "         and $($recCatalog.backupPath)"
Write-Host "  revert : content-mods\sakura-petals-everywhere\revert.ps1  (or safe-base\uninstall-all.ps1)"
Write-Host "Launch the game and visit the converted world(s) - the leaves should look like"
Write-Host "the sakura map's cherry petals (still fully pickable, counts unchanged)."
Write-Host "Note: Steam 'verify integrity of game files' restores the patched bundle and"
Write-Host "catalog.bin; just re-run apply.ps1 afterwards (safe-base still guards you)."
