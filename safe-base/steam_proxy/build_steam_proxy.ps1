# build_steam_proxy.ps1 — compile the auto-generated steam_proxy.cpp into
# steam_api64.dll (a stub proxy for safe-base's M2 offline layer).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Test-Path 'steam_proxy.cpp') -or -not (Test-Path 'steam_proxy.def')) {
    Write-Host "generating steam_proxy.cpp + steam_proxy.def from exports.txt..."
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'generate_proxy.ps1')
    if ($LASTEXITCODE -ne 0) { throw "generate_proxy.ps1 failed" }
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsroot  = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsroot) { throw "no VS install with VC tools found" }
$vcvars  = Join-Path $vsroot "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "missing $vcvars" }

$envOut = & $env:ComSpec /c "`"$vcvars`" >nul 2>&1 && set"
foreach ($line in $envOut) {
    if ($line -match '^(.+?)=(.*)$') { Set-Item -Path ("Env:" + $Matches[1]) -Value $Matches[2] -ErrorAction SilentlyContinue }
}
Write-Host "cl.exe -> $((Get-Command cl.exe -ErrorAction SilentlyContinue).Source)"

& cl.exe /nologo /W3 /O2 /MT /LD steam_proxy.cpp steam_proxy.def /link /OUT:steam_api64.dll
if ($LASTEXITCODE -ne 0) { throw "compile failed" }

# Clean MSVC byproducts so we only keep what's needed in the repo.
Remove-Item -Force -ErrorAction SilentlyContinue 'steam_proxy.obj'
if (Test-Path 'steam_proxy.lib') { Remove-Item -Force 'steam_proxy.lib' }
if (Test-Path 'steam_proxy.exp') { Remove-Item -Force 'steam_proxy.exp' }

$size = (Get-Item 'steam_api64.dll').Length
Write-Host "BUILD OK -> $PSScriptRoot\steam_api64.dll  ($size bytes)" -ForegroundColor Green
