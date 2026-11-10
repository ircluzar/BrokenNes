<#
.SYNOPSIS
  Builds the WideMess pointer-port test ROMs (docs/projects/widemess-spec.md section 10) into WideMess	estromsin:
    pointer.sfc            the ROM as a SNES image
    pointer.wms            the same game as a WideMess file (needs the pointer port, WMFEAT bit 6)
    pointer_nounlock.wms   the negative control: the same reads without the unlock (the registers stay a SNES's)
.PARAMETER Wla
  Folder holding wla-65816.exe and wlalink.exe. Default: $env:BROKENDEV_WLA, else the PVSnesLib toolchain beside this repo (PROJECTS\SuperVRUN_dev\snes-stub\toolchain\pvsneslib\devkitsnes\bin).
.PARAMETER Workshop
  BrokenNes.Workshop.exe (for --wms-pack). Default: $env:BROKENNES_WORKSHOP, else the newest build under C:\BrokenNes-work\bn_wms.
#>
param([string]$Wla = '', [string]$Workshop = '')
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $src '..\..\..')).Path
$bin = Join-Path $repo 'WideMess\testroms\bin'
New-Item -ItemType Directory -Force $bin | Out-Null

if (-not $Wla) { $Wla = $env:BROKENDEV_WLA }
if (-not $Wla) { $Wla = 'C:\BrokenNes-tools\supervrun\snes-stub\toolchain\pvsneslib\devkitsnes\bin' }
$asm = Join-Path $Wla 'wla-65816.exe'; $link = Join-Path $Wla 'wlalink.exe'
if (-not (Test-Path -LiteralPath $asm)) { throw "wla-65816.exe not found in $Wla (pass -Wla or set BROKENDEV_WLA)" }
if (-not $Workshop) { $Workshop = $env:BROKENNES_WORKSHOP }
if (-not $Workshop) {
    $Workshop = Get-ChildItem -Path (Join-Path $(if ($env:BROKENNES_WORK) { $env:BROKENNES_WORK } else { 'C:\BrokenNes-work' }) 'bn_wms') -Recurse -Filter 'BrokenNes.Workshop.exe' -ErrorAction SilentlyContinue |
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

# ---- pointer: the pointer port, and its negative control (no unlock)
Build-Image 'pointer.asm' 'pointer' @() (Join-Path $bin 'pointer.sfc')
Invoke-Tool $Workshop @('--wms-pack', '--in', (Join-Path $bin 'pointer.sfc'), '--out', (Join-Path $bin 'pointer.wms'), '--title', '"WideMess pointer"', '--features', '64') $src
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('pointer_nounlock_' + [guid]::NewGuid() + '.sfc')
Build-Image 'pointer.asm' 'pointer_nounlock' @('-DNOUNLOCK') $tmp
Invoke-Tool $Workshop @('--wms-pack', '--in', $tmp, '--out', (Join-Path $bin 'pointer_nounlock.wms'), '--title', '"WideMess pointer (no unlock)"', '--features', '0') $src
Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue
Get-ChildItem $bin -Filter 'pointer*' | Select-Object Name, Length
