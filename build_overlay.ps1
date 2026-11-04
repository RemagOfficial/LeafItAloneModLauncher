# build_overlay.ps1 — compile leaf_overlay.cpp into a version.dll proxy with MSVC x64.
$ErrorActionPreference = 'Stop'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsroot = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsroot) { throw "no VS install with VC tools" }
$vcvars = Join-Path $vsroot "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "missing $vcvars" }

$cmdOut = & $env:ComSpec /c "`"$vcvars`" >nul 2>&1 && set"
foreach ($line in $cmdOut) {
    if ($line -match '^(.+?)=(.*)$') {
        Set-Item -Path ("Env:" + $Matches[1]) -Value $Matches[2] -ErrorAction SilentlyContinue
    }
}
Write-Host "cl.exe -> $((Get-Command cl.exe -ErrorAction SilentlyContinue).Source)"
Set-Location "$PSScriptRoot"

& cl.exe /nologo /W3 /O2 /MT /EHsc /LD leaf_overlay.cpp version.def /link /OUT:version.dll
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
Write-Host "BUILD OK -> $PSScriptRoot\version.dll"
