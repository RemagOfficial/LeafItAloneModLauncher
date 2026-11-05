# base.psm1 - safe-base SDK for content mods (Leaf it Alone).
#
# WHAT THIS IS
#   A single self-contained PowerShell module that safe-base installs into
#   %LOCALAPPDATA%\LeafItAloneSafe\base.psm1. A content mod's apply.ps1 /
#   revert.ps1 does:
#
#       Import-Module "$env:LOCALAPPDATA\LeafItAloneSafe\base.psm1"
#       Assert-SafeBaseActive
#       Assert-SafePreconditions
#       $backups = Backup-Original -ModName 'bag-capacity' -TargetPath $someGameFile
#       # ...mod writes its replacement file(s) into place...
#       Register-Mod -Name 'bag-capacity' -Version '1.0.0' -Files $backups
#
#   so every content mod inherits the same safety story (firewall + Steam
#   proxy + save isolation + banner, all enforced by install.ps1) and the same
#   reversible-backup discipline, without duplicating any of it.
#
#   This file is deliberately STANDALONE: it does NOT dot-source common.ps1
#   (common.ps1 stays in the repo and is not shipped to the state dir), and it
#   recomputes its own paths. That keeps the deployed SDK importable from any
#   working directory with no repo dependency.
#
# THE CONTENT-MOD REGISTRY
#   %LOCALAPPDATA%\LeafItAloneSafe\mods.json is the source of truth for which
#   mods are currently applied. install.ps1's manifest no longer carries the
#   mod list; the registry is separate so registering/unregistering a content
#   mod never rewrites the safety manifest. uninstall.ps1 and status.ps1 read
#   it via this module; uninstall-all.ps1 reverts every entry then uninstalls.
#
#   Each registry entry records, per touched file, the absolute target path,
#   whether the file existed before (so a file the mod ADDED is deleted on
#   revert instead of restored), the original SHA-256 and the location of the
#   pristine backup. Restore never deletes a modified file: it parks it under
#   the mod's backup folder first, mirroring safe-base's own M2/M3/M4 policy.
#
# Requires: Windows PowerShell 5.1+ (works unchanged in 7). No admin for the
# registry/backup functions; reading the firewall rule for
# Assert-SafePreconditions works un-elevated.

$ErrorActionPreference = 'Stop'

# ---- state paths (self-contained; must match common.ps1) ----
$script:StateDir      = Join-Path $env:LOCALAPPDATA 'LeafItAloneSafe'
$script:ManifestPath  = Join-Path $script:StateDir 'manifest.json'
$script:ModsPath      = Join-Path $script:StateDir 'mods.json'
$script:ModBackupRoot = Join-Path $script:StateDir 'mod-backups'
$script:RuleName      = 'LeafItAloneSafe-BlockOutbound'
$script:SaveMarker    = '.safebase-empty'
$script:SdkVersion    = 1

function Get-SafeBasePaths {
    <#
        Returns the SDK's view of the safe-base state locations. Useful for a
        mod that wants to log where its backups live.
    #>
    [pscustomobject]@{
        StateDir      = $script:StateDir
        ManifestPath  = $script:ManifestPath
        ModsPath      = $script:ModsPath
        ModBackupRoot = $script:ModBackupRoot
    }
}

function Get-SafeBaseManifest {
    <#  Read the install manifest as a PSCustomObject, or $null if absent. #>
    if (-not (Test-Path -LiteralPath $script:ManifestPath)) { return $null }
    try { Get-Content -LiteralPath $script:ManifestPath -Raw | ConvertFrom-Json }
    catch { throw "safe-base manifest at $script:ManifestPath is unreadable: $($_.Exception.Message)" }
}

function Assert-SafeBaseActive {
    <#
        Throws unless safe-base is installed (manifest present and parseable).
        Every content mod should call this first. Returns the manifest object.
    #>
    if (-not (Test-Path -LiteralPath $script:ManifestPath)) {
        throw "safe-base is not installed (no manifest at $script:ManifestPath). Install it first: safe-base\install.ps1. Content mods must not modify the game outside the safe-base framework."
    }
    $m = Get-SafeBaseManifest
    if (-not $m) { throw "safe-base manifest exists but is empty/unreadable at $script:ManifestPath. Re-run install (or uninstall) before applying mods." }
    return $m
}

function Assert-SafePreconditions {
    <#
        Re-verifies the whole safety stack RIGHT NOW, from the manifest:
          M1  firewall rule present + enabled + Block + (program matches) + MpsSvc running
          M2  steam_api64.dll at the recorded path matches the installed proxy SHA
          M3  the Saves/ placeholder marker is present at the recorded path
          M4  version.dll at the game folder matches the installed overlay SHA

        Throws with every failed check on one message if anything is off;
        returns the manifest if all pass. Content mods call this before they
        touch a single game file, so they never apply into a broken base.
    #>
    param([switch]$AllowMissingOverlay)   # for -SkipOverlay installs
    $m = Assert-SafeBaseActive
    $problems = New-Object System.Collections.Generic.List[string]

    # --- M1 firewall rule ---
    $rule = Get-NetFirewallRule -Name $script:RuleName -ErrorAction SilentlyContinue
    if (-not $rule) {
        $problems.Add('M1: outbound-block rule is missing')
    } else {
        if ("$($rule.Enabled)" -ne 'True')        { $problems.Add('M1: rule is disabled') }
        if ("$($rule.Action)"   -ne 'Block')      { $problems.Add("M1: rule action is '$($rule.Action)', expected Block") }
        if ("$($rule.Direction)" -ne 'Outbound')  { $problems.Add("M1: rule direction is '$($rule.Direction)', expected Outbound") }
        $af = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $rule -ErrorAction SilentlyContinue
        if ($m.gameExe -and $af -and $af.Program) {
            $want = $m.gameExe.ToLowerInvariant().Trim('"')
            $have = $af.Program.ToLowerInvariant().Trim('"')
            if ($want -ne $have) { $problems.Add("M1: rule targets '$($af.Program)' but manifest recorded '$($m.gameExe)'") }
        }
    }
    $svc = Get-Service -Name 'MpsSvc' -ErrorAction SilentlyContinue
    if (-not $svc -or "$($svc.Status)" -ne 'Running') {
        $problems.Add('M1: Windows Firewall service (MpsSvc) is not running, so the rule is not enforced')
    }

    # --- M2 steam proxy ---
    if ($m.steamProxy) {
        $p = $m.steamProxy.realDllPath
        if (-not (Test-Path -LiteralPath $p)) {
            $problems.Add('M2: steam_api64.dll is absent from the game path')
        } else {
            $h = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToUpperInvariant()
            if ($h -ne $m.steamProxy.proxySha256) {
                $problems.Add('M2: the real steam_api64.dll is back (proxy not in place) - Steam uploads could go through')
            }
        }
    } else {
        $problems.Add('M2: manifest has no steamProxy entry (installed with -SkipSteam?)')
    }

    # --- M3 save isolation ---
    if ($m.saveIsolation) {
        $marker = Join-Path $m.saveIsolation.originalPath $script:SaveMarker
        if (-not (Test-Path -LiteralPath $marker)) {
            $problems.Add('M3: Saves/ isolation placeholder is missing - modded play would write to your real save')
        }
    } else {
        $problems.Add('M3: manifest has no saveIsolation entry (installed with -SkipSaves?)')
    }

    # --- M4 overlay ---
    if ($m.versionOverlay) {
        $vd = $m.versionOverlay.versionDllPath
        if (-not (Test-Path -LiteralPath $vd)) {
            $problems.Add('M4: overlay version.dll is missing - no on-screen OFFLINE banner')
        } else {
            $h = (Get-FileHash -LiteralPath $vd -Algorithm SHA256).Hash.ToUpperInvariant()
            if ($h -ne $m.versionOverlay.sourceDllSha256) {
                $problems.Add('M4: version.dll in place is not the build safe-base installed')
            }
        }
    } elseif (-not $AllowMissingOverlay) {
        $problems.Add('M4: manifest has no versionOverlay entry (installed with -SkipOverlay?)')
    }

    if ($problems.Count -gt 0) {
        $bullets = ($problems | ForEach-Object { "  - $_" }) -join "`r`n"
        throw "safe-base preconditions are NOT met; refusing to apply the mod.`r`n$bullets`r`n`r`nRun safe-base\status.ps1 for detail, or reinstall with safe-base\install.ps1."
    }
    return $m
}

# ---------------------------------------------------------------------------
# content-mod registry (mods.json)
# ---------------------------------------------------------------------------

function Get-SafeBaseModRegistry {
    <#  Returns the array of registered mod entries (empty if none). #>
    if (-not (Test-Path -LiteralPath $script:ModsPath)) { return @() }
    try {
        $j = Get-Content -LiteralPath $script:ModsPath -Raw | ConvertFrom-Json
    } catch {
        throw "content-mod registry at $script:ModsPath is unreadable: $($_.Exception.Message)"
    }
    if ($j -and $j.mods) { return @($j.mods) }
    return @()
}

function Write-SafeBaseModRegistry {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Mods)
    New-Item -Path $script:StateDir -ItemType Directory -Force | Out-Null
    $obj = [ordered]@{
        schemaVersion   = 1
        sdkVersion      = $script:SdkVersion
        lastModifiedAt  = (Get-Date).ToUniversalTime().ToString('o')
        mods            = @($Mods)
    }
    $tmp = "$script:ModsPath.tmp"
    ($obj | ConvertTo-Json -Depth 30) | Set-Content -LiteralPath $tmp -Encoding UTF8
    Move-Item -LiteralPath $tmp -Destination $script:ModsPath -Force
}

function Backup-Original {
    <#
        Snapshot ONE file a mod is about to overwrite or add, into the mod's
        private backup folder, BEFORE the mod writes. Returns a record the mod
        collects and passes to Register-Mod.

        -TargetPath  : absolute path of the game file (need not exist yet - an
                       added file produces a record with originalExists=false,
                       which Revert-Mod turns into a delete).
        -Label       : optional stable id used to name the backup copy; if
                       omitted the filename is used. A numeric suffix avoids
                       collisions when a mod touches two same-named files.
    #>
    param(
        [Parameter(Mandatory)][string]$ModName,
        [Parameter(Mandatory)][string]$TargetPath,
        [string]$Label
    )
    if (-not $Label) { $Label = [System.IO.Path]::GetFileName($TargetPath) }
    $modDir = Join-Path $script:ModBackupRoot $ModName
    New-Item -Path $modDir -ItemType Directory -Force | Out-Null

    $targetExists = Test-Path -LiteralPath $TargetPath
    $backupPath = $null
    $origSha = $null
    $origSize = 0

    if ($targetExists) {
        $item = Get-Item -LiteralPath $TargetPath
        $origSize = $item.Length
        $origSha  = (Get-FileHash -LiteralPath $TargetPath -Algorithm SHA256).Hash.ToUpperInvariant()
        # Pick a collision-free name under the mod folder.
        $safe = ($Label -replace '[^\w.\-]', '_')
        $candidate = Join-Path $modDir $safe
        $i = 1
        while (Test-Path -LiteralPath $candidate) {
            $candidate = Join-Path $modDir ("{0}.{1}" -f $safe, $i)
            $i++
        }
        Copy-Item -LiteralPath $TargetPath -Destination $candidate -Force
        $backupPath = (Resolve-Path -LiteralPath $candidate).ProviderPath
    }

    [ordered]@{
        targetPath      = $TargetPath
        label           = $Label
        originalExists  = $targetExists
        originalSha256  = $origSha
        originalSize    = $origSize
        backupPath      = $backupPath
        backedUpAt      = (Get-Date).ToUniversalTime().ToString('o')
    }
}

function Register-Mod {
    <#
        Record a mod (and the per-file backup records from Backup-Original) in
        mods.json. If an entry with the same name exists it is REPLACED (a
        mod reinstalling itself). Returns the stored entry.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Files,
        [string]$Version = '0.0.0',
        [string]$RevertScript,
        [string]$Notes
    )
    $modDir = Join-Path $script:ModBackupRoot $Name
    # Normalise each file record into a clean ordered hashtable.
    $normFiles = foreach ($f in $Files) {
        [ordered]@{
            targetPath     = "$($f.targetPath)"
            label          = "$($f.label)"
            originalExists = [bool]$f.originalExists
            originalSha256 = $f.originalSha256
            originalSize   = $f.originalSize
            backupPath     = $f.backupPath
            appliedSha256  = $(if ($f.targetPath -and (Test-Path -LiteralPath $f.targetPath)) { (Get-FileHash -LiteralPath $f.targetPath -Algorithm SHA256).Hash.ToUpperInvariant() } else { $null })
        }
    }
    $entry = [ordered]@{
        name      = $Name
        version   = $Version
        appliedAt = (Get-Date).ToUniversalTime().ToString('o')
        backupDir = $modDir
        files     = @($normFiles)
        revert    = $RevertScript
        notes     = $Notes
    }
    $mods = @(Get-SafeBaseModRegistry | Where-Object { $_.name -ne $Name })
    $mods += $entry
    Write-SafeBaseModRegistry -Mods $mods
    return [pscustomobject]$entry
}

function Unregister-Mod {
    <#
        Drop a mod's registry entry WITHOUT touching files. Normally called by
        Revert-Mod after its files are restored. Returns $true if an entry was
        removed, $false if the name was not registered.
    #>
    param([Parameter(Mandatory)][string]$Name)
    $mods = @(Get-SafeBaseModRegistry)
    $keep = @($mods | Where-Object { $_.name -ne $Name })
    if ($keep.Count -eq $mods.Count) {
        Write-Warning "Unregister-Mod: '$Name' is not registered (nothing removed)."
        return $false
    }
    Write-SafeBaseModRegistry -Mods $keep
    return $true
}

function Restore-ModFiles {
    <#
        Put a registered mod's files back the way they were, WITHOUT touching
        the registry entry. For each recorded file:
          * originalExisted + current target matches originalSha -> already
            reverted, skip.
          * originalExisted + current differs -> PARK the current (modded) file
            under the mod's backup folder, then move the pristine backup into
            place.
          * originalExisted + target absent (user deleted it) -> just restore
            the backup.
          * NOT originalExisted (the mod ADDED this file) -> if present, park
            it out of the game folder; the game goes back to not having it.
        Returns a report object. Never deletes anything.
    #>
    param([Parameter(Mandatory)][string]$Name)
    $mods = @(Get-SafeBaseModRegistry)
    $entry = $mods | Where-Object { $_.name -eq $Name } | Select-Object -First 1
    if (-not $entry) { throw "Restore-ModFiles: '$Name' is not registered." }
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
    $modDir = Join-Path $script:ModBackupRoot $Name
    New-Item -Path $modDir -ItemType Directory -Force | Out-Null

    $restored = New-Object System.Collections.Generic.List[string]
    $parked   = New-Object System.Collections.Generic.List[string]
    $skipped  = New-Object System.Collections.Generic.List[string]

    foreach ($f in @($entry.files)) {
        $target = "$($f.targetPath)"
        if ($f.originalExists) {
            if (Test-Path -LiteralPath $target) {
                $cur = (Get-FileHash -LiteralPath $target -Algorithm SHA256 -ErrorAction SilentlyContinue).Hash
                if ($cur -and $f.originalSha256 -and $cur.ToUpperInvariant() -eq $f.originalSha256) {
                    $skipped.Add($target); continue
                }
                # Modded (or hand-edited) file where the original belongs: park it.
                $pk = Join-Path $modDir ("{0}.modified-{1}" -f [System.IO.Path]::GetFileName($target), $stamp)
                Move-Item -LiteralPath $target -Destination $pk -Force
                $parked.Add($pk)
            }
            if ($f.backupPath -and (Test-Path -LiteralPath $f.backupPath)) {
                $parentDir = Split-Path -Parent $target
                if ($parentDir -and -not (Test-Path -LiteralPath $parentDir)) {
                    New-Item -Path $parentDir -ItemType Directory -Force | Out-Null
                }
                Move-Item -LiteralPath $f.backupPath -Destination $target -Force
                $restored.Add($target)
            } else {
                Write-Warning "Restore-ModFiles: backup missing for '$target' (looked in '$($f.backupPath)'). Original NOT restored."
            }
        } else {
            # File the mod added: remove it from the game by parking it.
            if (Test-Path -LiteralPath $target) {
                $pk = Join-Path $modDir ("{0}.added-{1}" -f [System.IO.Path]::GetFileName($target), $stamp)
                Move-Item -LiteralPath $target -Destination $pk -Force
                $parked.Add($pk)
                $restored.Add("$target (added-file removed)")
            }
        }
    }

    [pscustomobject]@{
        name            = $Name
        filesRestored   = @($restored)
        filesParked     = @($parked)
        filesUnchanged  = @($skipped)
    }
}

function Revert-Mod {
    <#
        Full revert of one mod: Restore-ModFiles + Unregister-Mod. This is what
        a mod's own revert.ps1 should call, and what uninstall-all.ps1 runs for
        every registered mod. Keeps the (now-empty-ish) backup folder so parked
        files remain inspectable. Returns the restore report.
    #>
    param([Parameter(Mandatory)][string]$Name)
    $report = Restore-ModFiles -Name $Name
    [void](Unregister-Mod -Name $Name)
    return $report
}

function Test-GameRunning {
    <#  True if 'Leaf it Alone' is running - mods must not mutate live files. #>
    [bool](Get-Process -Name 'Leaf it Alone' -ErrorAction SilentlyContinue)
}

Export-ModuleMember -Function `
    Get-SafeBasePaths, Get-SafeBaseManifest, `
    Assert-SafeBaseActive, Assert-SafePreconditions, `
    Get-SafeBaseModRegistry, Register-Mod, Unregister-Mod, `
    Backup-Original, Restore-ModFiles, Revert-Mod, Test-GameRunning
