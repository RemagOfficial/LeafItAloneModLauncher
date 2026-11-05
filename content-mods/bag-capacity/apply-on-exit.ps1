# apply-on-exit.ps1 - safe-base M8 companion for the in-game bag-capacity editor.
#
# The overlay (version.dll) can save new bag sizes while you play, but the bundle
# can only be re-patched while the game is CLOSED. So on Save the overlay spawns
# THIS tiny waiter: it blocks until Leaf it Alone exits, then runs apply.ps1 with
# the config the editor just wrote. Your new sizes are ready for the next launch.
#
# You can also run it by hand if you'd rather not wait on the overlay:
#   powershell -NoProfile -ExecutionPolicy Bypass -File apply-on-exit.ps1 `
#     -Config "$env:LOCALAPPDATA\LeafItAloneSafe\bag-capacity.json"
#
# It performs no action of its own - it only ever calls apply.ps1, which re-runs
# all the safe-base precondition checks. If those fail, nothing is patched.

[CmdletBinding()]
param(
    [string]$Config = (Join-Path $env:LOCALAPPDATA 'LeafItAloneSafe\bag-capacity.json'),
    [string]$ExeName = 'Leaf it Alone.exe',   # process to wait for
    [int]$MaxWaitMinutes = 1440                # bail after 24h so no stray waiter lives forever
)
$ErrorActionPreference = 'Stop'
$apply = Join-Path $PSScriptRoot 'apply.ps1'
$log   = Join-Path $env:LOCALAPPDATA 'LeafItAloneSafe\apply-on-exit.log'
function Say($t) { "$(Get-Date -Format o)  $t" | Out-File -FilePath $log -Append -Encoding UTF8 }

if (-not (Test-Path -LiteralPath $Config)) { Say "no config at $Config; exiting."; exit 0 }
Say "waiting for '$ExeName' to exit (config: $Config)"

$deadline = (Get-Date).AddMinutes($MaxWaitMinutes)
while ((Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
}
# brief settle so the OS releases the bundle file lock after the process dies
Start-Sleep -Seconds 3

Say "game closed; running apply.ps1 -Config '$Config' -Yes"
try {
    & $apply -Config $Config -Yes
    Say "apply.ps1 finished (exit $LASTEXITCODE)"
} catch {
    Say "apply.ps1 FAILED: $($_.Exception.Message)"
}
