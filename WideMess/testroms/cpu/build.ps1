<#
.SYNOPSIS
  Builds the WideMess milestone-4 test ROMs (CPU and memory) into WideMess\testroms\bin:
    speed.sfc            the fast-CPU / fast-DMA ROM as a SNES image (on a SNES: both halves of every measurement are equal)
    speed.wms            the same game as a WideMess file (map 0, LoROM; needs fast CPU and fast DMA)
    speed_nounlock.wms   the negative control: the same measurements without the unlock (a WideMess must stay at SNES timing)
    bigmap.wms           the WideMess-map ROM: 4 MB on map 2, 256 KB of battery RAM (needs 1 MB work RAM)
  The negative controls of bigmap (the same image cut to 2 MB, or on the HiROM map) are made from bigmap.wms by Workshop --wms-test itself.
.PARAMETER Wla
  Folder holding wla-65816.exe and wlalink.exe. Default: $env:BROKENDEV_WLA, else the PVSnesLib toolchain beside this repo (PROJECTS\SuperVRUN_dev\snes-stub\toolchain\pvsneslib\devkitsnes\bin).
.PARAMETER Workshop
  BrokenNes.Workshop.exe (for --wms-pack). Default: $env:BROKENNES_WORKSHOP, else the newest build under %LOCALAPPDATA%\VRUN_Nes_Dev_work\bn_wms.
#>
param([string]$Wla = '', [string]$Workshop = '')
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $src '..\..\..')).Path
$bin = Join-Path $repo 'WideMess\testroms\bin'
New-Item -ItemType Directory -Force $bin | Out-Null

if (-not $Wla) { $Wla = $env:BROKENDEV_WLA }
if (-not $Wla) { $Wla = Join-Path (Split-Path $repo -Parent) 'SuperVRUN_dev\snes-stub\toolchain\pvsneslib\devkitsnes\bin' }
$asm = Join-Path $Wla 'wla-65816.exe'; $link = Join-Path $Wla 'wlalink.exe'
if (-not (Test-Path -LiteralPath $asm)) { throw "wla-65816.exe not found in $Wla (pass -Wla or set BROKENDEV_WLA)" }
if (-not $Workshop) { $Workshop = $env:BROKENNES_WORKSHOP }
if (-not $Workshop) {
    $Workshop = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\bn_wms') -Recurse -Filter 'BrokenNes.Workshop.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Workshop -or -not (Test-Path -LiteralPath $Workshop)) { throw 'BrokenNes.Workshop.exe not found (pass -Workshop or set BROKENNES_WORKSHOP)' }

function Invoke-Tool([string]$exe, [string[]]$argList, [string]$cwd) {
    $out = Join-Path ([IO.Path]::GetTempPath()) ("wms_" + [guid]::NewGuid() + '.txt')
    $err = "$out.err"
    $p = Start-Process -FilePath $exe -ArgumentList $argList -WorkingDirectory $cwd -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err
    $text = (Get-Content -LiteralPath $out -Raw -ErrorAction SilentlyContinue) + (Get-Content -LiteralPath $err -Raw -ErrorAction SilentlyContinue)
    Remove-Item -LiteralPath $out, $err -ErrorAction SilentlyContinue
    if ($p.ExitCode -ne 0) { throw "$([IO.Path]::GetFileName($exe)) failed ($($p.ExitCode)): $text" }
    return $text
}

function Build-Image([string]$source, [string]$name, [string[]]$defines, [string]$outFile) {
    $obj = "$name.o"; $linkFile = Join-Path $src "$name.link"
    Invoke-Tool $asm (@($defines) + @('-o', $obj, $source)) $src | Out-Null
    Set-Content -LiteralPath $linkFile -Value "[objects]`r`n$obj" -Encoding ascii
    Invoke-Tool $link @('-r', $linkFile, $outFile) $src | Out-Null
    Remove-Item -LiteralPath (Join-Path $src $obj), $linkFile -ErrorAction SilentlyContinue
}

# ---- speed: fast CPU and fast DMA (LoROM), and its negative control
Build-Image 'speed.asm' 'speed' @() (Join-Path $bin 'speed.sfc')
Invoke-Tool $Workshop @('--wms-pack', '--in', (Join-Path $bin 'speed.sfc'), '--out', (Join-Path $bin 'speed.wms'), '--title', '"WideMess speed"', '--features', '40') $src
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('speed_nounlock_' + [guid]::NewGuid() + '.sfc')
Build-Image 'speed.asm' 'speed_nounlock' @('-DNOUNLOCK') $tmp
Invoke-Tool $Workshop @('--wms-pack', '--in', $tmp, '--out', (Join-Path $bin 'speed_nounlock.wms'), '--title', '"WideMess speed (no unlock)"', '--features', '0') $src
Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue

# ---- bigmap: the WideMess map, 4 MB of ROM (bank 0 assembled, banks 1-63 generated), 256 KB of battery RAM
$bank0 = Join-Path ([IO.Path]::GetTempPath()) ('bigmap_bank0_' + [guid]::NewGuid() + '.sfc')
$full = Join-Path ([IO.Path]::GetTempPath()) ('bigmap_' + [guid]::NewGuid() + '.sfc')
Build-Image 'bigmap.asm' 'bigmap' @() $bank0
& python -I (Join-Path $src 'gen_bigmap.py') $bank0 $full
if ($LASTEXITCODE -ne 0) { throw 'gen_bigmap.py failed' }
Invoke-Tool $Workshop @('--wms-pack', '--in', $full, '--out', (Join-Path $bin 'bigmap.wms'), '--title', '"WideMess big map"', '--features', '16', '--map', '2', '--sram', '262144', '--battery') $src
Remove-Item -LiteralPath $bank0, $full -ErrorAction SilentlyContinue
Get-ChildItem $bin | Select-Object Name, Length
