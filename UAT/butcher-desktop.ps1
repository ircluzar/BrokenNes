<#
.SYNOPSIS
  Butchering mode through the real desktop app (HTTP control API): the panel's own enter / Reload State / leave calls, and the battery-save freeze.

.DESCRIPTION
  Starts its OWN BrokenNes.Windows instance (a scratch build by default) with BROKENNES_SAVES_DIR pointing at a temp folder, so the user's battery
  saves, save states and presets are never touched, and every call is scoped to that PID (any other running BrokenNes is left alone).
  Per game:
    L1 leave      pause, take the entry state (POST /api/gh/base-state, as the panel does), resume, apply the Heavy built-in preset, Reload State with
                  effects on, then leave (POST /api/butcher/off + load the entry state) while paused: the picture must be the entry picture again.
    B1 battery    with the mode on, closing the ROM must NOT write its battery save (it is frozen while butchering); after Off it must save normally.
  Writes <Out>\desktop.txt (PASS / FAIL lines) and exits 1 on any failure.
#>
param(
    [string] $Exe,
    [string] $Out = (Join-Path $(if ($env:BROKENNES_WORK) { $env:BROKENNES_WORK } else { 'C:\BrokenNes-work' }) ("battery\desktop-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string] $Build = (Join-Path $(if ($env:BROKENNES_WORK) { $env:BROKENNES_WORK } else { 'C:\BrokenNes-work' }) 'battery-desktop-build')
)
$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib\ApiClient.ps1')
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$report = Join-Path $Out 'desktop.txt'; Set-Content -LiteralPath $report -Value '' -Encoding UTF8
$fails = 0
function Line($st, $sc, $what) { if ($st -eq 'FAIL') { $script:fails++ }; $l = "{0,-5} {1,-3} {2}" -f $st, $sc, $what; Add-Content -LiteralPath $report -Value $l; Write-Host $l -ForegroundColor $(switch ($st) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'DarkYellow' } }) }

if (-not $Exe) {
    Write-Host "building the desktop app into $Build (scratch: the copy you run is not touched)"
    & dotnet build (Join-Path $Repo 'Windows') -c Release --artifacts-path $Build 2>&1 | Select-String ' error ' | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    $Exe = Get-ChildItem -Path (Join-Path $Build 'bin') -Recurse -Filter 'BrokenNes.Windows.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Exe) { throw 'desktop build produced no BrokenNes.Windows.exe' }
}
$saves = Join-Path $Out 'saves'; New-Item -ItemType Directory -Force -Path $saves | Out-Null
$env:BROKENNES_SAVES_DIR = $saves

$corpus = Join-Path $Repo 'SubProjects\65-o-borked\bin\Debug\net8.0\roms'
$games = @(
    @{ c = 'nes';  rom = (Join-Path $corpus 'Legend of Zelda, The (U) (PRG1).nes') },
    @{ c = 'nes';  rom = (Join-Path $corpus 'Super Mario Bros. (JU) (PRG0) [!].nes') },
    @{ c = 'snes'; rom = 'X:\EMULATION\Super Nintendo\Super Nintendo (U)\Super Mario World (U) [!].smc' },
    @{ c = 'gb';   rom = 'X:\EMULATION\GameBoy\Super Mario Land (W) (V1.1) [!].zip' },
    @{ c = 'sms';  rom = 'X:\EMULATION\Sega Master System\Sonic The Hedgehog 2 (Europe) (v1.1).sms' },
    @{ c = 'md';   rom = 'X:\EMULATION\Sega Genesis\Sonic the Hedgehog (JUE) [!].bin' }
)
$presetDir = Join-Path $Repo 'Windows\Webmodules\ButcheringMode\builtins'

function Api($m, $p, $body = $null) {
    $a = @{ ProcessId = $script:pid0; Method = $m; Path = $p; TimeoutSec = 60 }
    if ($null -ne $body) { $a.Body = $body }
    Invoke-BrokenNesApi @a
}
function Fb { $r = Api GET '/api/ppu/framebuffer'; if ($r.data -is [string]) { return $r.data } return (($r.data | ForEach-Object { [byte]$_ }) -join ',') }
function Hash($s) { $sha = [System.Security.Cryptography.SHA1]::Create(); ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($s)))).Replace('-', '').Substring(0, 12) }

$inst = Start-BrokenNesInstance -ExePath $Exe -TimeoutSeconds 120
$script:pid0 = $inst.ProcessId
Write-Host "instance PID $pid0 at $($inst.BaseUrl), saves in $saves"
try {
    foreach ($g in $games) {
        $only2 = @($Only | ForEach-Object { $_ -split "," } | Where-Object { $_ }); if ($only2.Count -gt 0 -and $only2 -notcontains $g.c) { continue }
        $name = Split-Path -Leaf $g.rom
        if (-not (Test-Path -LiteralPath $g.rom)) { Line 'SKIP' 'L1' "$name (not on this machine)"; continue }
        try {
            $r = Api POST '/api/emulator/load-rom' @{ Path = $g.rom }
            if (-not $r.success) { Line 'FAIL' 'L1' "${name}: load-rom: $($r.error)"; continue }
            Start-Sleep -Seconds 6
            $reach = Api GET '/api/butcher/reach'
            if (-not $reach.success -or -not $reach.supported) { Line 'SKIP' 'L1' "${name}: Butchering mode not reachable ($($reach.reason)$($reach.error))"; continue }
            $presets = Get-Content (Join-Path $presetDir "$($g.c).json") -Raw | ConvertFrom-Json
            $heavy = $presets.Heavy

            # ---- L1: enter (paused), bend, Reload State with effects, leave while paused
            Api POST '/api/emulator/pause' | Out-Null; Start-Sleep -Milliseconds 300
            $b = Api POST '/api/gh/base-state' @{ Name = 'battery-test entry' }
            $bid = $b.baseState.Id; if (-not $bid) { $bid = $b.baseState.id }
            if (-not $b.success -or -not $bid) { Line 'FAIL' 'L1' "${name}: could not take the entry state: $($b.error)"; continue }
            $fb0 = Hash (Fb)
            Api POST '/api/emulator/resume' | Out-Null
            $s = Api POST '/api/butcher/settings' $heavy
            if (-not $s.success) { Line 'FAIL' 'L1' "${name}: settings refused: $($s.error)"; continue }
            Start-Sleep -Seconds 4
            $fbBent = Hash (Fb)
            $rl = Api POST '/api/gh/load-base' @{ id = $bid }
            if (-not $rl.success) { Line 'FAIL' 'L1' "${name}: Reload State failed: $($rl.error)"; continue }
            Start-Sleep -Seconds 3
            Api POST '/api/emulator/pause' | Out-Null; Start-Sleep -Milliseconds 300
            Api POST '/api/butcher/off' | Out-Null
            Api POST '/api/gh/load-base' @{ id = $bid } | Out-Null; Start-Sleep -Milliseconds 500
            $fb1 = Hash (Fb)
            $st = Api GET '/api/butcher/stats'
            $ok = $fb1 -eq $fb0
            Line $(if ($ok) { 'PASS' } else { 'FAIL' }) 'L1' ("{0}: leave restores the entry picture {1} (entry {2}, bent {3}, after leave {4}){5}" -f $name, $(if ($ok) { 'exactly' } else { 'NOT exactly' }), $fb0, $fbBent, $fb1, $(if ($fbBent -eq $fb0) { ' - note: Heavy did not change the picture' } else { '' }))
            Api POST '/api/emulator/resume' | Out-Null

            # ---- B1: battery frozen while butchering
            $con = Api GET '/api/console'
            $hasBattery = [bool]($con.status.hasBattery) -or ($g.c -eq 'nes' -and $name -like 'Legend of Zelda*')
            if (-not $hasBattery) { Line 'SKIP' 'B1' "${name}: no battery save"; continue }
            Start-Sleep -Seconds 3
            Api POST '/api/emulator/close-rom' | Out-Null; Start-Sleep -Seconds 2   # a normal save first, so a file exists
            $files0 = @(Get-ChildItem -LiteralPath $saves -Recurse -File | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })
            Api POST '/api/emulator/load-rom' @{ Path = $g.rom } | Out-Null; Start-Sleep -Seconds 5
            Api POST '/api/butcher/settings' $presets.'Broken (unsafe)' | Out-Null
            Start-Sleep -Seconds 4
            Api POST '/api/emulator/close-rom' | Out-Null; Start-Sleep -Seconds 2
            $files1 = @(Get-ChildItem -LiteralPath $saves -Recurse -File | Where-Object { $_.Name -notlike '*.state*' -and $_.DirectoryName -notlike '*Butchering*' } | ForEach-Object { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" })
            $changed = @($files1 | Where-Object { $files0 -notcontains $_ })
            Line $(if ($changed.Count -eq 0) { 'PASS' } else { 'FAIL' }) 'B1' ("{0}: battery save {1} while butchering{2}" -f $name, $(if ($changed.Count -eq 0) { 'stayed frozen' } else { 'WAS WRITTEN' }), $(if ($changed.Count) { ': ' + ($changed -join '; ') } else { " ($($files0.Count) save file(s) before)" }))
            Api POST '/api/butcher/off' | Out-Null
        }
        catch { Line 'FAIL' 'L1' "${name}: $($_.Exception.Message)" }
    }
}
finally {
    try { Api POST '/api/butcher/off' | Out-Null } catch { }
    Stop-BrokenNesInstance -ProcessId $pid0 | Out-Null
}
Add-Content -LiteralPath $report -Value "DONE $fails failure(s)"
Write-Host "report: $report"
if ($fails -gt 0) { exit 1 } else { exit 0 }
