# uninstall_overlay.ps1 — remove the overlay (version.dll + LeafOverlay.ini + log) and
# restore the pre-overlay backup (e.g. MelonLoader) if one was recorded.
$ErrorActionPreference = 'Stop'
$self  = Split-Path -Parent $MyInvocation.MyCommand.Path
$state = Join-Path $self '.install_state'

$Game = $null; $bak = $null
if (Test-Path $state) {
    foreach ($line in Get-Content $state) {
        if ($line -like 'game=*')   { $Game = $line -replace '^game=','' }
        if ($line -like 'backup=*') { $bak  = $line -replace '^backup=','' }
    }
}
if (-not $Game) {
    # Portable auto-detect (same as install): Steam libraryfolders.vdf + <drive>:\SteamLibrary
    $cands = @()
    foreach ($root in @("$env:ProgramFiles\Steam", "${env:ProgramFiles(x86)}\Steam", "$env:LOCALAPPDATA\Programs\Common\Steam")) {
        $vf = Join-Path $root 'libraryfolders.vdf'
        if (Test-Path $vf) {
            foreach ($m in (Select-String -Path $vf -Pattern '"path"\s*:\s*"([^"]+)"' -AllMatches).Matches) {
                $p = $m.Groups[1].Value -replace '\\\\','\'
                $cands += (Join-Path $p 'steamapps\common\Leaf it Alone')
            }
        }
    }
    foreach ($d in (Get-PSDrive -PSProvider FileSystem).Root) {
        $r = Join-Path $d 'SteamLibrary'
        if (Test-Path $r) { $cands += (Join-Path $r 'steamapps\common\Leaf it Alone') }
    }
    foreach ($c in $cands) { if (Test-Path (Join-Path $c 'Leaf it Alone.exe')) { $Game = $c; break } }
}
if (-not $Game) { Write-Host 'Could not locate the game folder. Run this from the install folder (it reads .install_state), or pass the path.'; exit 1 }
Write-Host "Game folder: $Game"

Get-Process -Name 'Leaf it Alone' -ErrorAction SilentlyContinue | ForEach-Object { throw "game is RUNNING (pid $($_.Id)); close it first" }

foreach ($f in @('version.dll','LeafOverlay.ini','LeafOverlay.log')) {
    $p = Join-Path $Game $f
    if (Test-Path $p) { Remove-Item -LiteralPath $p -Force; Write-Host "removed: $f" }
}

if ($bak -and (Test-Path $bak)) {
    foreach ($item in @('version.dll','MelonLoader','MelonLoader.Bootstrap.pdb','winmm.dll')) {
        $s = Join-Path $bak $item
        if (Test-Path $s) { Move-Item -LiteralPath $s -Destination (Join-Path $Game $item) -Force; Write-Host "restored: $item" }
    }
    Write-Host "restored pre-overlay files from $bak"
} else {
    Write-Host "no backup recorded; overlay files removed only."
}
if (Test-Path $state) { Remove-Item -LiteralPath $state -Force }
Write-Host "DONE. Game folder returned to pre-overlay state."
