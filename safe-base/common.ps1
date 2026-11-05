# common.ps1 — small helpers shared by install / uninstall / status.
# Dot-source this from other scripts: . "$PSScriptRoot\common.ps1"

Set-Variable -Name 'RuleName' -Value 'LeafItAloneSafe-BlockOutbound' -Option Constant
Set-Variable -Name 'RuleDisplay' -Value 'Leaf it Alone Safe (block outbound - modding)' -Option Constant
Set-Variable -Name 'RuleDesc' -Value ('Blocks outbound TCP+UDP from Leaf it Alone.exe so modded play never reaches the game''s leaderboards. Reversible via safe-base/uninstall.ps1. Does NOT affect Steam (separate process) or local loopback IPC.') -Option Constant
Set-Variable -Name 'StateDir' -Value (Join-Path $env:LOCALAPPDATA 'LeafItAloneSafe') -Option Constant
Set-Variable -Name 'ManifestPath' -Value (Join-Path $StateDir 'manifest.json') -Option Constant

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Find-GameExe {
    param([string]$Override)
    if ($Override) {
        if (-not (Test-Path -LiteralPath $Override)) { throw "override -GameExe path not found: $Override" }
        return (Resolve-Path -LiteralPath $Override).Path
    }
    $cands = @()
    # Steam program folders + libraryfolders.vdf
    foreach ($root in @("$env:ProgramFiles\Steam", "${env:ProgramFiles(x86)}\Steam", "$env:LOCALAPPDATA\Programs\Common\Steam")) {
        $vf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path $vf)) { $vf = Join-Path $root 'libraryfolders.vdf' }
        if (Test-Path $vf) {
            foreach ($m in (Select-String -Path $vf -Pattern '"path"\s*:\s*"([^"]+)"' -AllMatches).Matches) {
                $p = $m.Groups[1].Value -replace '\\\\','\'
                $cands += (Join-Path $p 'steamapps\common\Leaf it Alone\Leaf it Alone.exe')
            }
        }
    }
    # Common drive-root Steam libraries
    foreach ($d in (Get-PSDrive -PSProvider FileSystem).Root) {
        $cands += (Join-Path $d 'SteamLibrary\steamapps\common\Leaf it Alone\Leaf it Alone.exe')
    }
    foreach ($c in $cands) {
        if (Test-Path -LiteralPath $c) { return (Resolve-Path -LiteralPath $c).Path }
    }
    throw "Could not auto-detect 'Leaf it Alone.exe'. Pass -GameExe '<full path to Leaf it Alone\Leaf it Alone.exe>'."
}

function Get-FirewallState {
    $rule = Get-NetFirewallRule -Name $RuleName -ErrorAction SilentlyContinue
    $af   = if ($rule) { Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $rule -ErrorAction SilentlyContinue } else { $null }
    $svc  = Get-Service -Name MpsSvc -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Rule          = $rule
        Program       = if ($af) { $af.Program } else { $null }
        FwService     = $svc
        FwServiceStat = if ($svc) { $svc.Status.ToString() } else { 'Absent' }
    }
}
