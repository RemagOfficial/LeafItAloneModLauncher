# apply.ps1 - more-leaves content mod (v1.4.0, update-robust: glob bundle resolution +
#   filename/GUID-anchored catalog CRC nullify; coupled bank + scene + areas + runtime prefab + menu).
#
# WHAT ACTUALLY MAKES MORE LEAVES (proven in game, see MODLOG):
# Leaves are seeded from a FIXED bank of scatter positions, stored as a binary TextAsset
#   InitialLeaves_<World>  inside  coreassets_assets_leaves_<hash>.bundle
# format  [int32 count][count x 16-byte records], record = float32 x, y, z, <4th field=0.0>.
# The 4th field is always 0.0 (padding, NOT an area id) and the records are laid out as
# CONTIGUOUS PER-AREA BLOCKS in initArgs order (a survey showed the leading records are one
# tight same-height cluster). The spawner reads area i as bank[off_i : off_i + count_i],
# off_i being the running sum of the (scaled) per-area counts. So the bank must be grown by
# EXPANDING EACH RECORD IN PLACE (original + N-1 nearby copies, interleaved), never by
# appending copies at the tail - appending shifts every area's window into the next area and
# dumps all the copies on the last one, which was the non-monotonic 4x/6x/10x pickup bug.
# The bank count EQUALS each scene's leaf pool (Hometown 102400, Castle 140288,
# Sakura 260096). FOUR per-area count sources all equal the same value in vanilla and must
# be raised TOGETHER by the same integer N per world for a denser area that is WINNABLE:
#   1. the position BANK (patch_positions.py)  - positions new leaves can be placed at; each
#      record is expanded in place (interleaved) so the per-area blocks stay aligned;
#   2. the SCENE layout budget + top pool (patch_leaves.py, scenes_scenes_<W>) - how many
#      LEAVES SPAWN and the completion-goal denominator (the goal FOLLOWS this);
#   3. the ACTOR PREFAB layout budget + pool (patch_prefab.py, coreprefabs_assets_actor) -
#      the template the runtime LeafArea_<World> actors instantiate from, i.e. how many
#      leaves are REGISTERED AS PICKABLE. THIS was the pickup gate: leaving it at vanilla
#      spawned and demanded xN leaves but only the vanilla count could be raked up;
#   4. the AREAS registry leavesCount (patch_areas.py) - a named-area table; scaling it had
#      no observable effect but we keep it consistent with the others (harmless).
# All four at the same integer xN keep spawn, goal and pickable in lockstep, so the harvest
# is winnable at any multiplier.
#   5. the TITLE-SCREEN 'menu' scene (scenes_scenes_menu, patch_leaves.py --scale $sH) carries
#      a Hometown-shaped leaf layout (top 102400, 13 areas) and reads the SAME Hometown bank
#      by offset, so it MUST track the Hometown factor or the menu's slice lands on clustered
#      copies (the "title screen missing leaves + extras in 2 spots" symptom fixed in v1.3.1).


#
# MODES OF APPENDING (patch_positions.py):
#   scatter   (default) copies are JITTERED in x/z only (y/height and the 4th field kept),
#             so the added leaves are new nearby spots on the grass -> visibly denser.
#   dup-exact stacked copies (POC only). scatter is what we ship.
#
# PER WORLD: the same integer scale is applied to that world's scene budget (patch_leaves.py
# --scale N), its paired areas count (patch_areas.py --scale N) and its bank (patch_positions.py
# --scale-<world> N). The runtime PREFAB (patch_prefab.py) holds all three worlds in ONE bundle
# so it is patched once, scaling only the objects for worlds whose factor>1. A world at scale 1
# has its scene/areas left completely vanilla and its bank/prefab objects left byte-identical.
#
# This game loads bundles through Unity Addressables, which CRC-verifies every bundle
# against catalog.bin and silently hangs on a mismatch, so for EACH file we actually
# overwrite we null its catalog CRC (0 = "skip the check") via patch_catalog_null.py.
# We never hardcode CRCs or sizes (both move with updates): the nuller locates each
# record from the bundle's own filename/GUID on disk, and the live pristine size only
# confirms it. Addressables does NOT validate the size field, so a repacked
# (different-size) bundle still loads.
#
# Flow:
#   1. Import the deployed SDK; refuse if the game is running.
#   2. Assert-SafeBaseActive + Assert-SafePreconditions (never patch an unsafe base).
#   3. Locate the three scene bundles, the leaves bank and the Addressables catalog.
#   4. Resolve the per-world scales + scatter radius/seed (CLI > config > default 2/0.6).
#   5. If already applied, Revert-Mod to a clean vanilla baseline first (so re-applying
#      after editing the config re-scales vanilla, never stacks).
#   6. Build the WORK LIST = for each world whose scale>1 its scene bundle AND its paired
#      areas bundle, plus the shared bank (once, if any scale>1).
#   7. Stage each work item (each patcher self-verifies its own output); capture each
#      file's pristine size first. A failure here means the game folder is untouched.
#   8. Backup-Original every live file AND catalog (pristine, once), null each touched
#      bundle's CRC, overwrite, re-verify each LIVE file, Register-Mod every file. Any
#      failure after the backups restores ALL originals before registering.
#
# The scene bundles are large (castle ~80 MB, sakura ~287 MB); UnityPy repacks them
# faithfully in seconds-to-tens-of-seconds each, so apply is slower than a metadata mod.
# That is expected - a one-time, fully reversible write. Only worlds you leave at 1 skip
# the (slow, large) scene repack entirely.
#
# Steam "verify integrity of game files" restores the bundles + catalog.bin; just re-run
# apply.ps1 afterwards (safe-base still guards you).

[CmdletBinding()]
param(
    [string]$Config,                           # canonical = %LOCALAPPDATA%\LeafItAloneSafe\more-leaves.json (seeded from the repo template); pass to override
    [int]$ScaleHometown = 0,                    # >0 overrides the config's scales.hometown (integer density for Hometown)
    [int]$ScaleCastle = 0,                      # >0 overrides the config's scales.castle
    [int]$ScaleSakura = 0,                      # >0 overrides the config's scales.sakura
    [double]$ScatterRadius = -1,                # >=0 overrides the config's scatterRadius (metres, x/z)
    [int]$Seed = -1,                            # >=0 overrides the config's seed (deterministic scatter)
    [string]$BundlePath,                         # override auto-detected castle scene bundle (single-bundle manual test)
    [string]$BundlePath2,                        # override auto-detected hometown scene bundle
    [string]$BundlePath3,                        # override auto-detected sakura scene bundle
    [string]$BankPath,                           # override auto-detected coreassets_assets_leaves bank bundle
    [string]$PrefabPath,                         # override auto-detected coreprefabs_assets_actor layout bundle
    [string]$MenuPath,                           # override auto-detected scenes_scenes_menu (title screen) bundle
    [string]$CatalogPath,                        # override auto-detected catalog.bin
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

# 2. resolve paths. aa bundle dir = <game>\Leaf it Alone_Data\StreamingAssets\aa\StandaloneWindows64.
$modName    = 'more-leaves'
$modVersion = '1.4.0'
$gameFolder = if ($m.gameExe) { Split-Path -Parent $m.gameExe } else { $null }
$aaRel = 'Leaf it Alone_Data\StreamingAssets\aa\StandaloneWindows64'
# GLOB by stable prefix: a game update can rehash the <guid> in every bundle
# filename, so never pin the exact name. When an update leaves BOTH the old and
# the new bundle on disk, only the one the catalog stores as a PATH entry
# ("\...\<name>.bundle", the record with the crc/size) is what the game loads -
# prefer those; fall back to newest mtime if the catalog names none (or all).
$aaDirLive = if ($gameFolder) { Join-Path $gameFolder $aaRel } else { $null }
$catalogBytes = $null
$catalogText  = $null
if ($aaDirLive) {
    $catFile = Join-Path (Split-Path -Parent $aaDirLive) 'catalog.bin'
    if (Test-Path -LiteralPath $catFile) {
        $catalogBytes = [System.IO.File]::ReadAllBytes($catFile)
        $catalogText  = [System.Text.Encoding]::GetEncoding(28591).GetString($catalogBytes)
    }
}
function Test-CatalogPathEntry([string]$Name) {
    if (-not $catalogText) { return $false }
    return $catalogText.Contains('\' + $Name)
}
function Find-NewestBundle {
    param([string]$Prefix, [string]$Override, [string]$ParamName)
    if ($Override) { return $Override }
    if (-not $aaDirLive) { throw "manifest has no gameExe to locate the game folder; pass -$ParamName." }
    $hits = @(Get-ChildItem -LiteralPath $aaDirLive -Filter "$Prefix*.bundle" -File -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending)
    if ($hits.Count -eq 0) { return $null }
    if ($hits.Count -gt 1) {
        $ref = @($hits | Where-Object { Test-CatalogPathEntry $_.Name })
        if ($ref.Count -ge 1) {
            if ($ref.Count -lt $hits.Count) {
                Write-Warning "$($hits.Count) bundles match $Prefix*; the catalog references: $(($ref | ForEach-Object Name) -join ', ') (the others are update leftovers, skipped)."
            }
            $hits = $ref
        } else {
            Write-Warning "$($hits.Count) bundles match $Prefix*, none referenced in the catalog (half-updated install?); using the newest: $($hits[0].Name)"
        }
    }
    return $hits[0].FullName
}
$bankGlob  = 'coreassets_assets_leaves_'
$prefabGlob = 'coreprefabs_assets_actor_'
$menuGlob  = 'scenes_scenes_menu_'
$bankRel   = 'the leaves position bank (coreassets_assets_leaves_<guid>.bundle)'
$prefabRel = 'the actor prefab layout (coreprefabs_assets_actor_<guid>.bundle)'
$menuRel   = 'the title-screen scene (scenes_scenes_menu_<guid>.bundle)'

# The three gameplay scenes. The 'menu' scene is the TITLE SCREEN backdrop: it carries a
# leaf-area layout identical in total to Hometown (top 102400, 13 areas) and reads the SAME
# InitialLeaves_Hometown bank by offset. So when the hometown bank is expanded it MUST be
# scaled by the hometown factor too, else the title screen's slice lands on the clustered
# copies and shows "most leaves missing + extras in a couple of spots" (verified v1.3.0).
# Order is fixed: castle / hometown / sakura; per-world overrides line up with it.
# Each world ALSO has a paired AREAS bundle (the collectable leaf-count registry) that
# must be scaled in lockstep, else extra leaves spawn but cannot be picked up.
$scenes = @(
    [pscustomobject]@{ world = 'castle';   glob = 'scenes_scenes_castle_';   override = $BundlePath;   path = $null; areasGlob = 'coreassets_assets_areas1_'; areasPath = $null },
    [pscustomobject]@{ world = 'hometown'; glob = 'scenes_scenes_hometown_'; override = $BundlePath2; path = $null; areasGlob = 'coreassets_assets_areas2_'; areasPath = $null },
    [pscustomobject]@{ world = 'sakura';   glob = 'scenes_scenes_sakura_';   override = $BundlePath3; path = $null; areasGlob = 'coreassets_assets_areas_';  areasPath = $null }
)
foreach ($s in $scenes) {
    $s.path = Find-NewestBundle -Prefix $s.glob -Override $s.override -ParamName 'BundlePath'
    # the areas bundle always sits in the SAME dir as its scene bundle
    $s.areasPath = if ($s.path) { Find-NewestBundle -Prefix $s.areasGlob -Override $null -ParamName 'BundlePath' } else { $null }
}
$bankBundle   = Find-NewestBundle -Prefix $bankGlob  -Override $BankPath   -ParamName 'BankPath'
$prefabBundle = Find-NewestBundle -Prefix $prefabGlob -Override $PrefabPath -ParamName 'PrefabPath'
$menuBundle   = Find-NewestBundle -Prefix $menuGlob  -Override $MenuPath   -ParamName 'MenuPath'

if (-not $CatalogPath) {
    if (-not $aaDirLive) { throw "manifest has no gameExe to locate the game folder; pass -CatalogPath." }
    # catalog.bin sits at <...>/StreamingAssets/aa/catalog.bin, one level above the bundle dir
    $CatalogPath = Join-Path (Split-Path -Parent $aaDirLive) 'catalog.bin'
}
$missing = @()
foreach ($p in (@($scenes[0].path, $scenes[1].path, $scenes[2].path,
                  $scenes[0].areasPath, $scenes[1].areasPath, $scenes[2].areasPath,
                  $bankBundle, $prefabBundle, $menuBundle))) {
    if (-not $p -or -not (Test-Path -LiteralPath $p)) { $missing += $p }
}
if ($missing.Count -gt 0) {
    Write-Error ("target bundle(s) not found (searched:`n  {0}`n):`n{1}`nGame may have updated or is installed elsewhere. Pass the -BundlePath/-BankPath overrides." -f $aaDirLive, (($missing | ForEach-Object { "  $_" }) -join "`n"))
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

$patcher     = Join-Path $PSScriptRoot 'patch_leaves.py'
$areasPatcher = Join-Path $PSScriptRoot 'patch_areas.py'
$prefabPatcher = Join-Path $PSScriptRoot 'patch_prefab.py'
$posPatcher  = Join-Path $PSScriptRoot 'patch_positions.py'
$catNull     = Join-Path $PSScriptRoot 'patch_catalog_null.py'
if (-not (Test-Path -LiteralPath $patcher))      { throw "scene patcher missing at $patcher" }
if (-not (Test-Path -LiteralPath $areasPatcher)) { throw "areas patcher missing at $areasPatcher" }
if (-not (Test-Path -LiteralPath $prefabPatcher)){ throw "prefab patcher missing at $prefabPatcher" }
if (-not (Test-Path -LiteralPath $posPatcher))   { throw "position patcher missing at $posPatcher" }
if (-not (Test-Path -LiteralPath $catNull))      { throw "catalog nuller missing at $catNull" }

# 3. resolve per-world integer scales + scatter params.
#    Precedence: per-world CLI > config scales.<world> > default 2.
#    radius/seed: CLI > config > default 0.6 / 1337.
function Resolve-Scale([int]$cli, $cfgVal) {
    if ($cli -gt 0) { return $cli }
    if ($null -ne $cfgVal -and [int]$cfgVal -ge 1) { return [int]$cfgVal }
    return 2
}
$sH = 2; $sC = 2; $sS = 2; $radius = 0.6; $seed = 1337
if (-not $Config) {
    $liveCfg = Join-Path $env:LOCALAPPDATA 'LeafItAloneSafe\more-leaves.json'
    $repoCfg = Join-Path $PSScriptRoot 'more-leaves.json'
    if (-not (Test-Path -LiteralPath $liveCfg)) {
        New-Item -Path (Split-Path -Parent $liveCfg) -ItemType Directory -Force | Out-Null
        if (Test-Path -LiteralPath $repoCfg) {
            $seedCfg = Get-Content -LiteralPath $repoCfg -Raw
        } else {
            $seedCfg = '{"scales":{"hometown":2,"castle":2,"sakura":2},"scatterRadius":0.6,"seed":1337}'
        }
        Set-Content -LiteralPath $liveCfg -Value $seedCfg -Encoding UTF8
        Write-Host "seeded the live config at $liveCfg" -ForegroundColor DarkGray
    }
    $Config = $liveCfg
}
if (-not (Test-Path -LiteralPath $Config)) {
    Write-Error "no config at $Config and no -Scale* given. Edit more-leaves.json, or pass -ScaleHometown/-ScaleCastle/-ScaleSakura."
    exit 7
}
$cfg = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
$cH = if ($cfg.scales) { $cfg.scales.hometown } else { $null }
$cC = if ($cfg.scales) { $cfg.scales.castle }   else { $null }
$cS = if ($cfg.scales) { $cfg.scales.sakura }   else { $null }
$sH = Resolve-Scale $ScaleHometown $cH
$sC = Resolve-Scale $ScaleCastle   $cC
$sS = Resolve-Scale $ScaleSakura   $cS
if ($cfg.PSObject.Properties.Name -contains 'scatterRadius' -and $cfg.scatterRadius -ge 0) { $radius = [double]$cfg.scatterRadius }
if ($cfg.PSObject.Properties.Name -contains 'seed') { $seed = [int]$cfg.seed }
if ($ScatterRadius -ge 0) { $radius = $ScatterRadius }
if ($Seed -ge 0) { $seed = $Seed }
if ($sH -lt 1 -or $sC -lt 1 -or $sS -lt 1) { Write-Error "scales must each be >= 1 (got H=$sH C=$sC S=$sS)"; exit 7 }

# 4. if it's already applied, revert to vanilla first, then re-patch from the pristine
# backups. This makes "edit the config -> run apply again" a clean loop, not a stack.
$existing = @(Get-SafeBaseModRegistry | Where-Object { $_.name -eq $modName })
if ($existing.Count -gt 0) {
    Write-Host "'$modName' is already applied - reverting to vanilla first, then re-applying." -ForegroundColor Yellow
    Revert-Mod -Name $modName | Out-Null
}

# 5. build the WORK LIST. A world at scale>1 is touched in BOTH its scene bundle (visuals +
# goal) and its paired areas bundle (collectable registry) - scaling one without the other
# leaves leaves that spawn but can't be picked up. The (shared) bank is touched once if ANY
# world scale>1; its per-world scales are passed in one pass so worlds left at 1 stay
# byte-identical inside the bank too.
$work = @()
foreach ($s in $scenes) {
    $sc = switch ($s.world) { 'castle' { $sC } 'hometown' { $sH } 'sakura' { $sS } }
    if ($sc -gt 1) {
        $work += [pscustomobject]@{ kind = 'scene'; world = $s.world; path = $s.path;      scale = $sc }
        $work += [pscustomobject]@{ kind = 'areas'; world = $s.world; path = $s.areasPath; scale = $sc }
    }
}
if (($sH -gt 1) -or ($sC -gt 1) -or ($sS -gt 1)) {
    $work += [pscustomobject]@{ kind = 'bank';   world = '(all)'; path = $bankBundle;   scale = 0 }
    # the actor prefab holds all three worlds' layouts in one bundle -> patch once, per-world
    $work += [pscustomobject]@{ kind = 'prefab'; world = '(all)'; path = $prefabBundle; scale = 0 }
}
# The title-screen 'menu' scene reads the Hometown bank, so it must be scaled by the SAME
# factor as Hometown (patched with patch_leaves, scale = $sH) whenever Hometown is denser;
# at $sH == 1 the hometown bank is left whole and the menu stays byte-identical (vanilla).
if ($sH -gt 1) {
    $work += [pscustomobject]@{ kind = 'menu'; world = 'hometown'; path = $menuBundle; scale = $sH }
}
if ($work.Count -eq 0) {
    Write-Warning "every world scale is 1 - nothing to do (that is just vanilla). Set a scale > 1 in more-leaves.json or pass -ScaleHometown/-ScaleCastle/-ScaleSakura."
    exit 0
}

Write-Host ""
Write-Host "more-leaves apply (v1.4.0 update-robust glob + nullify; bank-in-place + scene + prefab layout + areas + menu, scatter)" -ForegroundColor Cyan
Write-Host ("  scales  : hometown x{0}  castle x{1}  sakura x{2}" -f $sH, $sC, $sS) -ForegroundColor Magenta
Write-Host ("  scatter : radius {0} m (x/z only, y preserved)  seed {1}" -f $radius, $seed)
Write-Host ("  files   :")
foreach ($w in $work) { Write-Host ("            [{0}] {1}" -f $w.kind, (Split-Path -Leaf $w.path)) }
Write-Host "  catalog : $CatalogPath  (nulls the CRC of every file touched above)"
Write-Host "  safety  : M1 + M2 + M3 + M4 verified via Assert-SafePreconditions"
Write-Host "  note    : sakura (~287 MB) repack can take tens of seconds; that is normal."
Write-Host ""
if (-not $Yes) {
    $reply = Read-Host "Type YES to patch these bundles + catalog (anything else aborts)"
    if ($reply -ne 'YES') { Write-Host "aborted"; exit 0 }
}

# 6. stage every patched file first (each self-verifies); a failure here means the game
# folder is still untouched.
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("leaf-moreleaves-" + [guid]::NewGuid().ToString('N'))
New-Item -Path $staging -ItemType Directory -Force | Out-Null
$records = @()
try {
    for ($i = 0; $i -lt $work.Count; $i++) {
        $w = $work[$i]
        $b = $w.path
        $wn = Split-Path -Leaf $b                          # resolved bundle name for logs
        $sz = (Get-Item -LiteralPath $b).Length        # pristine size, before overwrite -> catalog anchor
        $mb = [math]::Round($sz / 1MB)
        $sub = Join-Path $staging ("b$i")
        New-Item -Path $sub -ItemType Directory -Force | Out-Null

        if ($w.kind -eq 'bank') {
            Write-Host "[patch] $wn (~$mb MB) -> staging (BANK scatter H$sH/C$sC/S$sS, r=$radius)..." -ForegroundColor Cyan
            $patchArgs = @($posPatcher, '--in', $b, '--out-dir', $sub, '--mode', 'scatter',
                            '--scale-hometown', $sH, '--scale-castle', $sC, '--scale-sakura', $sS,
                            '--radius', $radius, '--seed', $seed)
        } elseif ($w.kind -eq 'areas') {
            Write-Host "[patch] $wn -> staging (AREAS collectable count x$($w.scale))..." -ForegroundColor Cyan
            $patchArgs = @($areasPatcher, '--in', $b, '--out-dir', $sub, '--scale', $w.scale)
        } elseif ($w.kind -eq 'prefab') {
            Write-Host "[patch] $wn -> staging (PREFAB runtime leaf-area layout H$sH/C$sC/S$sS)..." -ForegroundColor Cyan
            $patchArgs = @($prefabPatcher, '--in', $b, '--out-dir', $sub,
                           '--scale-hometown', $sH, '--scale-castle', $sC, '--scale-sakura', $sS)
        } elseif ($w.kind -eq 'menu') {
            Write-Host "[patch] $wn (~$mb MB) -> staging (TITLE/MENU budget x$($w.scale), tracks hometown bank)..." -ForegroundColor Cyan
            $patchArgs = @($patcher, '--in', $b, '--out-dir', $sub, '--scale', $w.scale)
        } else {
            Write-Host "[patch] $wn (~$mb MB) -> staging (scene budget x$($w.scale))..." -ForegroundColor Cyan
            $patchArgs = @($patcher, '--in', $b, '--out-dir', $sub, '--scale', $w.scale)
        }
        $patchOut = & $Python @patchArgs
        $code = $LASTEXITCODE
        Write-Host ($patchOut | Out-String)
        if ($code -ne 0) { throw "patcher exited $code for $b; game folder untouched." }
        $produced = Get-ChildItem -LiteralPath $sub -Filter '*.bundle' -File | Select-Object -First 1
        if (-not $produced) { throw "patcher reported success but produced no staged bundle for $b" }
        # stash staged path + pristine size back onto the work item
        $w | Add-Member -NotePropertyName staged   -NotePropertyValue $produced.FullName
        $w | Add-Member -NotePropertyName origSize -NotePropertyValue $sz
    }

    # 7. back up the LIVE files AND catalog (pristine) BEFORE touching any of them.
    Write-Host "[backup] snapshotting the live bundles + catalog.bin via the SDK..." -ForegroundColor Cyan
    $recCatalog = Backup-Original -ModName $modName -TargetPath $CatalogPath
    foreach ($w in $work) { $records += (Backup-Original -ModName $modName -TargetPath $w.path) }
    $records += $recCatalog

    try {
        # 8. null each touched bundle's CRC in the catalog (update-robust: the
        # record is located from the bundle's own filename/GUID, no CRC pinned).
        foreach ($w in $work) {
            $wn = Split-Path -Leaf $w.path
            Write-Host "[catalog] nullifying CRC for $wn (filename/GUID-anchored)..." -ForegroundColor Cyan
            $catOut = & $Python $catNull --catalog $CatalogPath --bundle-path $w.path
            $catCode = $LASTEXITCODE
            Write-Host ($catOut | Out-String)
            if ($catCode -ne 0) { throw "catalog nuller exited $catCode for $wn" }
        }

        # 9. overwrite the live bundles with the staged (verified) ones.
        foreach ($w in $work) {
            Write-Host "[apply] replacing $(Split-Path -Leaf $w.path)..." -ForegroundColor Cyan
            Copy-Item -LiteralPath $w.staged -Destination $w.path -Force
        }

        # 10. re-read each LIVE file to prove the applied bytes are correct (survey exits
        # non-zero if the layout object / bank is missing or an invariant broke).
        foreach ($w in $work) {
            Write-Host "[verify] re-reading live $(Split-Path -Leaf $w.path)..." -ForegroundColor Cyan
            if ($w.kind -eq 'bank') {
                $live = (& $Python $posPatcher --in $w.path --verify-only) -join "`n"
            } elseif ($w.kind -eq 'areas') {
                $live = (& $Python $areasPatcher --in $w.path --verify-only) -join "`n"
            } elseif ($w.kind -eq 'prefab') {
                $live = (& $Python $prefabPatcher --in $w.path --verify-only) -join "`n"
            } else {
                $live = (& $Python $patcher --in $w.path --verify-only) -join "`n"
            }
            $liveCode = $LASTEXITCODE
            if ($liveCode -ne 0) { throw "post-apply verify exited $liveCode for $($w.path)" }
            Write-Host ($live | Out-String)
        }
    } catch {
        # any failure after the backups: restore EVERY original so the game is never left
        # half-patched, then re-throw.
        Write-Warning "Apply failed ($_); restoring the pristine bundle + catalog backups."
        foreach ($rec in $records) {
            if (Test-Path -LiteralPath $rec.backupPath) {
                Copy-Item -LiteralPath $rec.backupPath -Destination $rec.targetPath -Force
            }
        }
        throw
    }

    # 11. register every file so safe-base can reverse the whole change.
    $notes = "more-leaves v1.4.0 scatter: bank(in-place)+scene+prefab+areas+menu H$sH/C$sC/S$sS (r=$radius seed=$seed); $($work.Count) catalog CRCs nullified"
    Register-Mod -Name $modName -Version $modVersion -Files $records `
        -RevertScript (Join-Path $PSScriptRoot 'revert.ps1') -Notes $notes | Out-Null
} finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "OK - more-leaves v1.4.0 applied." -ForegroundColor Green
Write-Host ("  density : hometown x{0}  castle x{1}  sakura x{2}  (scatter r={3} m)" -f $sH, $sC, $sS, $radius)
Write-Host "  Launch and rake - the ground should be visibly denser with leaves, the goal"
Write-Host "  rises to match, and (now that the bank is expanded IN PLACE so each area's"
Write-Host "  positions stay in its own block, and the runtime leaf-area prefab is scaled too)"
Write-Host "  the extra leaves are actually PICKABLE and winnable at ANY multiplier."
if ($sH -gt 1) {
    Write-Host "  the title-screen 'menu' backdrop is scaled with Hometown too, so it stays"
    Write-Host "  whole (no missing leaves) instead of reading a broken slice of the bank."
}
Write-Host "Revert any time with revert.ps1 (safe-base restores the bundles + catalog.bin)."
