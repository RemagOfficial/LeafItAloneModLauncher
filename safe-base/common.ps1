# common.ps1 — small helpers shared by install / uninstall / status.
# Dot-source this from other scripts: . "$PSScriptRoot\common.ps1"

Set-Variable -Name 'RuleName' -Value 'LeafItAloneSafe-BlockOutbound' -Option Constant
Set-Variable -Name 'RuleDisplay' -Value 'Leaf it Alone Safe (block outbound - modding)' -Option Constant
Set-Variable -Name 'RuleDesc' -Value ('Blocks outbound TCP+UDP from Leaf it Alone.exe so modded play never reaches the game''s leaderboards. Reversible via safe-base/uninstall.ps1. Does NOT affect Steam (separate process) or local loopback IPC.') -Option Constant
Set-Variable -Name 'StateDir' -Value (Join-Path $env:LOCALAPPDATA 'LeafItAloneSafe') -Option Constant
Set-Variable -Name 'ManifestPath' -Value (Join-Path $StateDir 'manifest.json') -Option Constant
Set-Variable -Name 'BackupDir' -Value (Join-Path $StateDir 'backup') -Option Constant
Set-Variable -Name 'SteamProxySource' -Value (Join-Path $PSScriptRoot 'steam_proxy\steam_api64.dll') -Option Constant

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

# ---------------------------------------------------------------------------
# M2 - steam_api64.dll proxy helpers
# ---------------------------------------------------------------------------

# Returns the game folder from a game exe path.
function Get-GameFolderFromExe {
    param([Parameter(Mandatory)][string]$ExePath)
    (Resolve-Path -LiteralPath $ExePath).ProviderPath | Split-Path -Parent
}

# Finds the real steam_api64.dll next to the game's install.
# Unity games usually place it either in the exe folder or in
# <GameFolder>/<GameFolder>_Data/Plugins/x86_64/. Preference order matters so
# we install the proxy where P/Invoke (DllImport) actually resolves it.
function Find-GameSteamDll {
    param([Parameter(Mandatory)][string]$GameExePath)
    $root = Get-GameFolderFromExe -ExePath $GameExePath
    $cands = @(
        (Join-Path $root 'steam_api64.dll'),
        (Join-Path $root 'Leaf it Alone_Data\Plugins\x86_64\steam_api64.dll'),
        (Join-Path $root 'Leaf it Alone_Data\Plugins\steam_api64.dll')
    )
    foreach ($c in $cands) {
        if (Test-Path -LiteralPath $c) { return (Resolve-Path -LiteralPath $c).ProviderPath }
    }
    # Fallback: recursive scan.
    $found = Get-ChildItem -LiteralPath $root -Recurse -Filter 'steam_api64.dll' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($found) { return $found.FullName }
    throw "no steam_api64.dll found under $root (game may not use Steamworks, or is installed differently)"
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

# Classifies the steam_api64.dll currently at $Path:
#   'proxy' - our stub proxy (matches the source in safe-base/steam_proxy)
#   'real'  - the game's original (matches a backup recorded in the manifest,
#             OR is significantly larger than the proxy)
#   'unknown' - something else (user swapped in a different DLL)
function Classify-SteamDll {
    param([Parameter(Mandatory)][string]$Path, [string]$ProxySha)
    if (-not (Test-Path -LiteralPath $Path)) { return 'absent' }
    $h = Get-FileSha256 -Path $Path
    if ($ProxySha -and $h -eq $ProxySha) { return 'proxy' }
    # Fallback: the real steam_api64.dll is 300+ KB. Our proxy is ~160 KB.
    $sz = (Get-Item -LiteralPath $Path).Length
    if ($sz -gt 200000) { return 'real' }
    if ($sz -lt 200000 -and $sz -gt 100000) { return 'proxy' }
    return 'unknown'
}

# Back up the real DLL into $BackupDir\steam_api64.dll, drop the proxy at
# the original path. Returns a hashtable with all the info install.ps1 stores
# in the manifest.
function Install-SteamProxy {
    param(
        [Parameter(Mandatory)][string]$RealDllPath,
        [Parameter(Mandatory)][string]$ProxySource
    )
    if (-not (Test-Path -LiteralPath $ProxySource)) {
        throw "proxy DLL missing: $ProxySource (run safe-base\steam_proxy\build_steam_proxy.ps1)"
    }
    New-Item -Path $BackupDir -ItemType Directory -Force | Out-Null
    $backup = Join-Path $BackupDir 'steam_api64.dll'

    # Refuse to overwrite an existing backup - means a previous install is
    # still active or the user has stray state.
    if (Test-Path -LiteralPath $backup) {
        throw "backup already exists at $backup. Uninstall first."
    }

    Move-Item -LiteralPath $RealDllPath -Destination $backup -Force
    Copy-Item -LiteralPath $ProxySource -Destination $RealDllPath -Force

    $realSha   = Get-FileSha256 -Path $backup
    $proxySha  = Get-FileSha256 -Path $RealDllPath
    $realSize  = (Get-Item -LiteralPath $backup).Length
    $proxySize = (Get-Item -LiteralPath $RealDllPath).Length

    @{
        realDllPath       = $RealDllPath
        realDllBackupPath = $backup
        realDllSize       = $realSize
        realDllSha256     = $realSha
        proxySourcePath   = (Resolve-Path -LiteralPath $ProxySource).ProviderPath
        proxySize         = $proxySize
        proxySha256       = $proxySha
        installedAt       = (Get-Date).ToUniversalTime().ToString('o')
    }
}

# Restore the real DLL from the backup and remove the backup.
# Refuses if the current DLL doesn't look like our proxy (so we never
# overwrite a DLL we didn't install).
function Restore-SteamProxy {
    param(
        [Parameter(Mandatory)][string]$RealDllPath,
        [Parameter(Mandatory)][string]$BackupPath,
        [string]$ExpectedProxySha
    )
    if (-not (Test-Path -LiteralPath $BackupPath)) {
        Write-Warning "no steam_api64.dll backup at $BackupPath - nothing to restore"
        return $false
    }
    if (Test-Path -LiteralPath $RealDllPath) {
        $kind = Classify-SteamDll -Path $RealDllPath -ProxySha $ExpectedProxySha
        if ($kind -notin @('proxy')) {
            Write-Warning "steam_api64.dll at '$RealDllPath' is '$kind' (not our proxy). Refusing to overwrite. Backup remains at '$BackupPath'."
            return $false
        }
        Remove-Item -LiteralPath $RealDllPath -Force
    }
    Move-Item -LiteralPath $BackupPath -Destination $RealDllPath -Force
    return $true
}
