# BrokenDev compat adapter: BrokenNes itself, the reference column. Dot-sourced by lib\Compat.ps1.
#
# The same headless Workshop 'verify' uses, through 'BrokenNes.Workshop.exe --brokendev run --spec <spec>' (Workshop\BrokenDevCli.cs): the
# same cores (the project's verify.cores, else the app defaults), the same power-on RAM the project's verify scenario asks for, and the
# battery save in the spec's savedir (brokennes.sav). BrokenNes is started with a scratch BROKENNES_CONFIG_DIR so the user's own config
# (deliberate core mixes) never applies.

Set-StrictMode -Version Latest

function Invoke-BdAdapterWorkshop {
    param([Parameter(Mandatory)]$Emu, $Package, [Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$SpecPath, [Parameter(Mandatory)]$Spec,
          [Parameter(Mandatory)][string]$Scratch, [int]$TimeoutSec = 600, [int]$HangSec = 60,
          # from the project's verify recipe: powerOnRam (NES), cores, ntscFrameTiming
          [string]$PowerOnRam = '', $Cores = $null, [switch]$Ntsc)
    # --manifests is not needed by 'run'; it makes a Workshop older than 'run' reach its command switch and say "Unknown BrokenDev command"
    $argv = @('--brokendev', 'run', '--spec', $SpecPath, '--manifests', (Get-BdProjectsDir))
    if ($PowerOnRam) { $argv += @('--ram', $PowerOnRam) }
    if ($Cores) { foreach ($k in 'cpu', 'ppu', 'apu') { $v = [string](Get-BdProp $Cores $k ''); if ($v) { $argv += @("--$k", $v) } } }
    if ($Ntsc) { $argv += '--ntsc' }
    $cfg = Join-Path $Scratch 'brokennes-config'
    New-Item -ItemType Directory -Force -Path $cfg | Out-Null
    $r = Invoke-BdWatchedProcess -FilePath $Exe -Arguments $argv -WorkingDirectory $Scratch -Env @{ BROKENNES_DEV = '1'; BROKENNES_CONFIG_DIR = $cfg; BROKENDEV_HOME = (Get-BdHome) } `
        -ResultFile (Join-Path ([string]$Spec.Out) 'result.txt') -TimeoutSec $TimeoutSec -HangSec $HangSec -LogFile (Join-Path $Scratch 'workshop.log')
    if ($r.ExitCode -eq 2 -and $r.Output -match "Unknown BrokenDev command 'run'") {
        throw "CANNOT-RUN: the Workshop at $Exe predates 'brokendev run' (the compat runner): rebuild it with brokendev.ps1 build-workshop"
    }
    if ($r.ExitCode -eq 2) { throw "the Workshop refused the run (exit 2): $(Select-BdTail $r.Output 4)" }
    $settings = [ordered]@{ workshop = $Exe; workshopBuilt = (Get-Item -LiteralPath $Exe).LastWriteTime.ToString('yyyy-MM-dd HH:mm'); configDir = $cfg
                            powerOnRam = $(if ([string]$Spec.Console -eq 'nes') { if ($PowerOnRam) { $PowerOnRam } else { 'fceux (BrokenNes default: 00 00 00 00 FF FF FF FF)' } } else { 'zeros' })
                            cores = $(if ($Cores) { ($Cores | ConvertTo-Json -Compress) } else { 'the app defaults' }); ntscFrameTiming = [bool]$Ntsc }
    $r | Add-Member -NotePropertyName Settings -NotePropertyValue $settings -Force
    $r
}
