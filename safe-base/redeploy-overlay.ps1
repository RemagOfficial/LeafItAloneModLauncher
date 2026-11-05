# redeploy-overlay.ps1 - safe-base M8 mod-support helper.
#
# Once safe-base is installed, its version.dll overlay lives in the GAME folder
# and the M4 banner / F8 bag-editor are compiled into it. Iterating on the overlay
# (a new banner, a new editor, ...) or picking up a rebuilt version.dll should not
# mean uninstalling the whole safety framework (firewall + Steam proxy + save
# isolation) just to swap one DLL. install.ps1 deliberately refuses a double
# install, so this is the sanctioned one-command refresh.
#
# What it does, and nothing else:
#   1. Refuse if the game is running (version.dll is loaded by the game).
#   2. Require safe-base to be installed (reads the M4 block of the manifest).
#   3. Overwrite <game>\version.dll IN PLACE with the freshly built
#      overlay\version.dll (the pristine original is already in the M4 backup, so
#      this does NOT re-back-up - it just updates our own deployed file).
#   4. Regenerate <game>\LeafOverlay.ini from the current template, so new
#      keys/sections show up on already-installed games. Any hand
#      edits you made to that ini are replaced; the previous copy is parked next
#      to it as LeafOverlay.ini.bak-<timestamp>.
#   5. Refresh the manifest's overlay SHA/size so uninstall/status stay accurate.
#
# It touches NEITHER the firewall rule, the Steam proxy, nor your saves. Safe to
# re-run any time you rebuild the overlay. Run build_overlay.ps1 first.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File safe-base\redeploy-overlay.ps1
#   # or point at a different build:  ... -SourceDll ..\overlay\version.dll

[CmdletBinding()]
param(
    [string]$SourceDll    # default: overlay\version.dll (the tracked build output)
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"

if (-not $SourceDll) { $SourceDll = $OverlaySourceDll }
if (-not (Test-Path -LiteralPath $SourceDll)) {
    Write-Error "overlay version.dll missing at $SourceDll - run overlay\build_overlay.ps1 first."
    exit 2
}

# 1. the game must be closed (its version.dll is locked while it runs).
if (Get-Process -Name 'Leaf it Alone' -ErrorAction SilentlyContinue) {
    Write-Error "Leaf it Alone is running. Close the game before redeploying the overlay."
    exit 3
}

# 2. safe-base must be installed (we need the M4 overlay block of the manifest).
if (-not (Test-Path -LiteralPath $ManifestPath)) {
    Write-Error "no safe-base manifest at $ManifestPath. Install safe-base first: safe-base\install.ps1"
    exit 4
}
$man = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if (-not $man.versionOverlay) {
    Write-Error "safe-base manifest has no versionOverlay block (installed with -SkipOverlay?). Reinstall with the overlay."
    exit 5
}
$dllPath  = $man.versionOverlay.versionDllPath
$iniPath  = $man.versionOverlay.overlayIniPath
$proxySize = [long]$man.versionOverlay.expectedProxySize
if (-not (Test-Path -LiteralPath $dllPath)) {
    Write-Error "installed version.dll not found at $dllPath. Something removed it from the game folder; run install.ps1 (after uninstall.ps1) instead."
    exit 6
}

Write-Host ""
Write-Host "redeploy safe-base overlay" -ForegroundColor Cyan
Write-Host "  source : $SourceDll ($((Get-Item -LiteralPath $SourceDll).Length) bytes)"
Write-Host "  ->     : $dllPath"
Write-Host "  ini    : $iniPath"
Write-Host "  (firewall / steam proxy / saves are NOT touched)"
Write-Host ""

# 3. overwrite our own deployed DLL in place (the true original is already backed
#    up by M4 in $BackupDir\overlay, so there is nothing to re-snapshot).
Copy-Item -LiteralPath $SourceDll -Destination $dllPath -Force
$newSha  = Get-FileSha256 -Path $SourceDll
$newSize = (Get-Item -LiteralPath $SourceDll).Length

# 4. regenerate the ini from the current template; park any hand-edited copy.
if (Test-Path -LiteralPath $iniPath) {
    $parked = "$iniPath.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
    Copy-Item -LiteralPath $iniPath -Destination $parked -Force
    Write-Host "  parked your current ini at $parked" -ForegroundColor DarkGray
}
$ini = Format-SafeBaseOverlayIni -ExpectedSteamProxySize $proxySize
[System.IO.File]::WriteAllText($iniPath, $ini, [System.Text.Encoding]::ASCII)

# 5. refresh the manifest so status.ps1 / uninstall.ps1 stay byte-accurate.
#    Use Add-Member -Force: the object came from ConvertFrom-Json, so in Windows
#    PowerShell 5.1 a plain assignment to a NEW property (redeployedAt) throws.
$vo = $man.versionOverlay
$vo | Add-Member -Force NoteProperty sourceDllPath   (Resolve-Path -LiteralPath $SourceDll).ProviderPath
$vo | Add-Member -Force NoteProperty sourceDllSize   $newSize
$vo | Add-Member -Force NoteProperty sourceDllSha256 $newSha
$vo | Add-Member -Force NoteProperty redeployedAt    (Get-Date).ToUniversalTime().ToString('o')
$man | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8

Write-Host ""
Write-Host "OK - overlay redeployed. version.dll = $newSize bytes (sha256 $($newSha.Substring(0,12))...)." -ForegroundColor Green
Write-Host "Launch the game: the banner shows, and the bag-capacity editor is toggled with F8."
Write-Host "To undo all of safe-base (firewall + proxy + saves + overlay): safe-base\uninstall.ps1"
