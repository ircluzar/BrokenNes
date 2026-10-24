<#
.SYNOPSIS
  Builds the WideMess "8 layers" test ROM and its negative control into WideMess\testroms\bin:
    layers.sfc            the SNES-format image (a SNES shows a red screen: no WideMess there)
    layers.wms            the WideMess game file (header + image)
    layers_nounlock.wms   the negative control: writes the WideMess registers without the unlock (a WideMess must ignore them)
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

& python -I (Join-Path $src 'gen_data.py') $src
if ($LASTEXITCODE -ne 0) { throw 'gen_data.py failed' }

foreach ($v in @(@{ Name = 'layers'; Defines = @() }, @{ Name = 'layers_nounlock'; Defines = @('-DNOUNLOCK') })) {
    $obj = "$($v.Name).o"; $linkFile = Join-Path $src "$($v.Name).link"; $sfc = Join-Path $bin "$($v.Name).sfc"
    Invoke-Tool $asm (@($v.Defines) + @('-o', $obj, 'layers.asm')) $src | Out-Null
    Set-Content -LiteralPath $linkFile -Value "[objects]`r`n$obj" -Encoding ascii
    Invoke-Tool $link @('-r', $linkFile, $sfc) $src | Out-Null
    Remove-Item -LiteralPath (Join-Path $src $obj), $linkFile -ErrorAction SilentlyContinue
    $title = if ($v.Name -eq 'layers') { 'WideMess 8 layers' } else { 'WideMess 8 layers (no unlock)' }
    Invoke-Tool $Workshop @('--wms-pack', '--in', $sfc, '--out', (Join-Path $bin "$($v.Name).wms"), '--title', "`"$title`"", '--features', '1') $src
}
# the negative control is used as a .wms only
Remove-Item -LiteralPath (Join-Path $bin 'layers_nounlock.sfc') -ErrorAction SilentlyContinue
Get-ChildItem $bin | Select-Object Name, Length
