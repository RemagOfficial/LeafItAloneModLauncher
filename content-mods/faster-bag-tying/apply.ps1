# apply.ps1 - faster-bag-tying content mod. Makes tying a leaf bag faster by
# lowering the base `sealingTime` on the four bag actors (Small/Medium/Big/Giant
# LeafBag) in the coreassets ACTORS bundle, through the safe-base SDK so the
# change is gated on the safety framework, backed up, and reversible.
#
# The in-game "faster bag tying" upgrades are percentage-based (-20/-20/-20/-15%)
# stat modifiers you have to buy; this mod drops the BASE tying time instead, so
# bags tie faster from the start. Times are the vanilla 1.0/1.35/1.7/1.8 s scaled
# by a multiplier (default 0.5 = half the time = 2x faster) or set explicitly.
#
# Seconds come from the canonical config
# %LOCALAPPDATA%\LeafItAloneSafe\faster-bag-tying.json (a multiplier OR explicit
# per-bag seconds, plus an alsoEmptying flag), seeded from the repo template on
# first run - the launcher writes this same file. -Scale / -Small.. / -AlsoEmptying
# on the command line override it for one run. Re-running apply.ps1 after editing
# the config reverts to vanilla first, then re-patches from the pristine backups.
#
# This game loads bundles through Unity Addressables, which CRC-verifies every
# bundle against catalog.bin and silently hangs on a mismatch, so the mod also
# nulls THIS bundle's catalog CRC (0 = "skip the check") - see
# patch_catalog_null.py, which locates the record from the bundle's own
# filename/GUID/size on disk, so a game update that rehashes the CRC cannot
# break it (nothing is hardcoded).
#
#   1. Import the deployed SDK and refuse if the game is running.
#   2. Assert-SafeBaseActive + Assert-SafePreconditions (safety verified now).
#   3. Locate the target actors bundle + the Addressables catalog.bin.
#   4. Resolve the four target tying seconds (CLI overrides > config file).
#   5. If already applied, Revert-Mod to a clean vanilla baseline first.
#   6. Run patch_faster_bag_tying.py into staging. It refuses unless the bundle
#      holds vanilla values and self-verifies its output.
#   7. Backup-Original the live bundle AND catalog.bin, null the catalog CRC,
#      overwrite the bundle, re-read the LIVE bundle to confirm, Register-Mod both
#      files. Any failure after the backups restores BOTH originals.

[CmdletBinding()]
param(
    [string]$Config,                          # canonical = %LOCALAPPDATA%\LeafItAloneSafe\faster-bag-tying.json (seeded from the repo template); pass to override
    [double]$Scale = 0,                       # >0 overrides the config (fraction of the vanilla 1.0/1.35/1.7/1.8 tying time, in (0,1])
    [double]$Small = 0, [double]$Medium = 0, [double]$Big = 0, [double]$Giant = 0, # >0 override config seconds (give all four)
    [switch]$AlsoEmptying,                    # also scale emptyingDuration by the same per-bag ratio
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
$modName    = 'faster-bag-tying'
$modVersion = '1.1.0'
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
    $hits = @(Get-ChildItem -LiteralPath $aaDirLive -Filter 'coreassets_assets_actors_*.bundle' -File -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending)
    if ($hits.Count -gt 1 -and $catalogText) {
        $ref = @($hits | Where-Object { $catalogText.Contains('\' + $_.Name) })
        if ($ref.Count -ge 1) { $hits = $ref }
    }
    if ($hits.Count -eq 0) {
        Write-Error "no coreassets_assets_actors_*.bundle found in:`n  $aaDirLive`nGame may have updated or is installed elsewhere. Pass -BundlePath."
        exit 4
    }
    if ($hits.Count -gt 1) { Write-Warning "$($hits.Count) actors bundles on disk (an update can leave the old one); using the newest: $($hits[0].Name)" }
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

# 3. resolve the four target tying seconds. Precedence: explicit -Small.. >
# -Scale > the config file. Always computed against the vanilla 1.0/1.35/1.7/1.8.
$vanilla = [ordered]@{ small = 1.0; medium = 1.35; big = 1.7; giant = 1.8 }
$alsoEmpty = [bool]$AlsoEmptying
if ($Small -gt 0 -or $Medium -gt 0 -or $Big -gt 0 -or $Giant -gt 0) {
    if (-not ($Small -gt 0 -and $Medium -gt 0 -and $Big -gt 0 -and $Giant -gt 0)) {
        Write-Error "when setting explicit tying seconds you must give all of -Small/-Medium/-Big/-Giant."
        exit 7
    }
    $targets = [ordered]@{ small = $Small; medium = $Medium; big = $Big; giant = $Giant }
    $sizeSource = 'command line -Small/-Medium/-Big/-Giant seconds'
}
elseif ($Scale -gt 0) {
    if ($Scale -gt 1) { Write-Error "-Scale must be a fraction in (0,1] (a share of the vanilla tying time); got $Scale." ; exit 7 }
    $targets = [ordered]@{
        small  = [Math]::Round($vanilla.small  * $Scale, 4)
        medium = [Math]::Round($vanilla.medium * $Scale, 4)
        big    = [Math]::Round($vanilla.big    * $Scale, 4)
        giant  = [Math]::Round($vanilla.giant  * $Scale, 4)
    }
    $sizeSource = "command line -Scale $Scale"
}
else {
    # No explicit seconds / -Scale given: read the config. The canonical file is
    # %LOCALAPPDATA%\LeafItAloneSafe\faster-bag-tying.json (the same one the
    # launcher writes); it is seeded from the repo template on first run.
    if (-not $Config) {
        $liveCfg = Join-Path $env:LOCALAPPDATA 'LeafItAloneSafe\faster-bag-tying.json'
        $repoCfg = Join-Path $PSScriptRoot 'faster-bag-tying.json'
        if (-not (Test-Path -LiteralPath $liveCfg)) {
            New-Item -Path (Split-Path -Parent $liveCfg) -ItemType Directory -Force | Out-Null
            if (Test-Path -LiteralPath $repoCfg) {
                $seed = Get-Content -LiteralPath $repoCfg -Raw | ConvertFrom-Json
            } else {
                $seed = [pscustomobject]@{ mode = 'multiplier'; multiplier = 0.5; alsoEmptying = $false }
            }
            $seed | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $liveCfg -Encoding UTF8
            Write-Host "seeded the live config at $liveCfg" -ForegroundColor DarkGray
        }
        $Config = $liveCfg
    }
    if (-not (Test-Path -LiteralPath $Config)) {
        Write-Error "no config at $Config and no -Scale/-Small.. given. Edit faster-bag-tying.json, or pass -Scale / -Small.. values."
        exit 7
    }
    $cfg = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
    if ($cfg.PSObject.Properties.Name -contains 'alsoEmptying') { $alsoEmpty = [bool]$cfg.alsoEmptying }
    if ($cfg.mode -eq 'seconds') {
        $s = $cfg.seconds
        if (-not $s.small -or -not $s.medium -or -not $s.big -or -not $s.giant) {
            Write-Error "config mode 'seconds' needs seconds.small/medium/big/giant all set: $Config"
            exit 7
        }
        $targets = [ordered]@{ small = [double]$s.small; medium = [double]$s.medium; big = [double]$s.big; giant = [double]$s.giant }
        $sizeSource = "config $Config (mode 'seconds')"
    }
    else {
        $mult = if ($cfg.multiplier) { [double]$cfg.multiplier } else { 0.5 }
        if ($mult -le 0 -or $mult -gt 1) { Write-Error "config multiplier must be in (0,1]; got $mult" ; exit 7 }
        $targets = [ordered]@{
            small  = [Math]::Round($vanilla.small  * $mult, 4)
            medium = [Math]::Round($vanilla.medium * $mult, 4)
            big    = [Math]::Round($vanilla.big    * $mult, 4)
            giant  = [Math]::Round($vanilla.giant  * $mult, 4)
        }
        $sizeSource = "config $Config (mode 'multiplier' x$mult)"
    }
}

# 4. if it's already applied, revert to vanilla first, then re-patch from the
# pristine backups. This makes "edit the config -> run apply again" a clean loop.
$existing = @(Get-SafeBaseModRegistry | Where-Object { $_.name -eq $modName })
if ($existing.Count -gt 0) {
    Write-Host "'$modName' is already applied - reverting to vanilla first, then re-applying." -ForegroundColor Yellow
    Revert-Mod -Name $modName | Out-Null
}

Write-Host ""
Write-Host "faster-bag-tying apply" -ForegroundColor Cyan
Write-Host "  bundle   : $BundlePath"
Write-Host "  catalog  : $CatalogPath  (nulls this bundle's CRC record so Addressables loads the repack)"
Write-Host "  seconds  : small=$($targets.small)  medium=$($targets.medium)  big=$($targets.big)  giant=$($targets.giant)"
Write-Host "  emptying : $(if ($alsoEmpty) { 'also scaled by the same ratio' } else { 'left at vanilla' })"
Write-Host "  source   : $sizeSource"
Write-Host "  safety   : M1 + M2 + M3 + M4 verified via Assert-SafePreconditions"
Write-Host ""
if (-not $Yes) {
    $reply = Read-Host "Type YES to patch this bundle + catalog (anything else aborts)"
    if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
}

# 5. stage the patch. The patcher validates the vanilla input and verifies its
# own output; a non-zero exit means the game folder was never touched.
$patcher    = Join-Path $PSScriptRoot 'patch_faster_bag_tying.py'
$catPatcher = Join-Path $PSScriptRoot 'patch_catalog_null.py'
if (-not (Test-Path -LiteralPath $patcher))    { throw "patcher missing at $patcher" }
if (-not (Test-Path -LiteralPath $catPatcher)) { throw "catalog patcher missing at $catPatcher" }
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("leaf-bagtie-" + [guid]::NewGuid().ToString('N'))
New-Item -Path $staging -ItemType Directory -Force | Out-Null

# the catalog record is located update-robustly from the bundle's own
# filename/GUID/size, so nothing about the install needs to be pinned here.
$recBundle = $null; $recCatalog = $null
try {
    Write-Host "[patch] running UnityPy patcher into staging..." -ForegroundColor Cyan
    $patchArgs = @($patcher, '--in', $BundlePath, '--out-dir', $staging,
                   '--small', $targets.small, '--medium', $targets.medium,
                   '--big', $targets.big, '--giant', $targets.giant)
    if ($alsoEmpty) { $patchArgs += '--also-emptying' }
    $patchOut = & $Python @patchArgs
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
        #    filename/GUID/size (no hardcoded CRC), is idempotent, and refuses
        #    on ambiguity - so it's fine if a prior run already zeroed it.
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
        $live = (& $Python $patcher --in $BundlePath --verify-only) -join "`n"
        $liveCode = $LASTEXITCODE
        Write-Host $live
        if ($liveCode -ne 0) { throw "post-apply bundle verify exited $liveCode" }
    } catch {
        Write-Warning "Apply failed ($_); restoring the pristine bundle + catalog backups."
        Copy-Item -LiteralPath $recBundle.backupPath  -Destination $BundlePath  -Force
        Copy-Item -LiteralPath $recCatalog.backupPath -Destination $CatalogPath -Force
        throw
    }

    # 10. register both files so safe-base can reverse the whole change.
    Register-Mod -Name $modName -Version $modVersion -Files @($recBundle, $recCatalog) `
        -RevertScript (Join-Path $PSScriptRoot 'revert.ps1') `
        -Notes "bag tying seconds small=$($targets.small) medium=$($targets.medium) big=$($targets.big) giant=$($targets.giant)$(if ($alsoEmpty) { ' (+ emptying scaled)' }) (+ catalog CRC nullified)" | Out-Null
} finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "OK - faster-bag-tying applied. Tying is now small=$($targets.small)s medium=$($targets.medium)s big=$($targets.big)s giant=$($targets.giant)s." -ForegroundColor Green
Write-Host "  backups: $($recBundle.backupPath)"
Write-Host "         and $($recCatalog.backupPath)"
Write-Host "  revert : content-mods\faster-bag-tying\revert.ps1  (or safe-base\uninstall-all.ps1)"
Write-Host "Launch the game and tie a full bag - the sealing animation should be quicker."
Write-Host "Note: Steam 'verify integrity of game files' restores both the bundle and"
Write-Host "catalog.bin; just re-run apply.ps1 afterwards (safe-base still guards you)."
