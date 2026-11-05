# install_overlay.ps1 — install the Leaf it Alone native overlay example mod.
# Backs up any existing MelonLoader/version.dll into the workspace, then drops in
# our version.dll + LeafOverlay.ini. Fully reversible with uninstall_overlay.ps1.
param([string]$Game)
$ErrorActionPreference = 'Stop'
$self   = Split-Path -Parent $MyInvocation.MyCommand.Path
$src    = Join-Path $self 'version.dll'
$ini    = Join-Path $self 'LeafOverlay.ini'
$wsRoot = Split-Path -Parent $self
$state  = Join-Path $self '.install_state'

function Find-Game {
    $cands = @()
    # Steam libraryfolders.vdf paths
    foreach ($root in @("$env:ProgramFiles\Steam", "${env:ProgramFiles(x86)}\Steam", "$env:LOCALAPPDATA\Programs\Common\Steam")) {
        $vf = Join-Path $root 'libraryfolders.vdf'
        if (Test-Path $vf) {
            Select-String -Path $vf -Pattern '"path"\s+"([^"]+)"' | ForEach-Object {
                $p = $_.Matches.Groups[1].Value -replace '\\\\','\'
                $cands += (Join-Path $p 'steamapps\common\Leaf it Alone')
            }
        }
    }
    # every filesystem drive's SteamLibrary
    foreach ($r in (Get-PSDrive -PSProvider FileSystem).Root) {
        $cands += (Join-Path $r 'SteamLibrary\steamapps\common\Leaf it Alone')
    }
    foreach ($c in $cands) { if (Test-Path (Join-Path $c 'Leaf it Alone.exe')) { return $c } }
    return $null
}

if (-not $Game) { $Game = Find-Game }
if (-not $Game -or -not (Test-Path (Join-Path $Game 'Leaf it Alone.exe'))) {
    throw "Could not locate 'Leaf it Alone'. Re-run with: -Game 'X:\...\Leaf it Alone'"
}
Write-Host "Game folder: $Game"

if (-not (Test-Path $src)) { throw "overlay version.dll not built: $src (run build_overlay.ps1)" }
if (Test-Path $state) { Write-Host "Already installed (see .install_state). Run uninstall_overlay.ps1 first."; exit 0 }
Get-Process -Name 'Leaf it Alone' -ErrorAction SilentlyContinue | ForEach-Object { throw "game is RUNNING (pid $($_.Id)); close it first" }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$bak   = Join-Path $wsRoot "backup\pre-overlay-$stamp"
New-Item -ItemType Directory -Force -Path $bak | Out-Null

# back up (uninstall) any prior loader occupying our slot
foreach ($item in @('version.dll','MelonLoader','MelonLoader.Bootstrap.pdb','winmm.dll')) {
    $p = Join-Path $Game $item
    if (Test-Path $p) { Move-Item -LiteralPath $p -Destination $bak -Force; Write-Host "backed up: $item" }
}

Copy-Item -LiteralPath $src -Destination (Join-Path $Game 'version.dll') -Force
Write-Host "installed: version.dll"
if (-not (Test-Path (Join-Path $Game 'LeafOverlay.ini'))) {
    Copy-Item -LiteralPath $ini -Destination (Join-Path $Game 'LeafOverlay.ini') -Force
    Write-Host "installed: LeafOverlay.ini (edit it to customize)"
} else {
    Write-Host "kept existing LeafOverlay.ini"
}

"game=$Game" | Set-Content -LiteralPath $state
"backup=$bak" | Add-Content -LiteralPath $state
Write-Host ""
Write-Host "DONE. Launch the game via Steam. The overlay shows in the top-left; press F9 to toggle."
Write-Host "Config: $Game\LeafOverlay.ini   Log: $Game\LeafOverlay.log"
