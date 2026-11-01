<#
.SYNOPSIS
  Builds the WideMess test cartridge (WideMess/testcart/README.md) into WideMess\testroms\bin (or -Out):
    widemess-testcart.wms                 the cartridge: the WideMess map (map 2), 512 KB, no required features (it reads WMFEAT and says what it lacks)
    widemess-testcart.sfc                 the same image as a SNES HiROM file: on a SNES it shows its "this is a WideMess cartridge" screen
    widemess-testcart.<page>.expect.png   the pictures its reference pages must draw, from milestone 2's model of the spec
.PARAMETER Wla
  Folder holding wla-65816.exe, wla-spc700.exe and wlalink.exe. Default: $env:BROKENDEV_WLA, else the PVSnesLib toolchain beside this repo
  (PROJECTS\SuperVRUN_dev\snes-stub\toolchain\pvsneslib\devkitsnes\bin), found from a .claude\worktrees checkout too.
.PARAMETER Workshop
  BrokenNes.Workshop.exe (for --wms-pack). Default: $env:BROKENNES_WORKSHOP, $env:BD_WORKSHOP, else the newest build under %LOCALAPPDATA%\VRUN_Nes_Dev_work.
.PARAMETER Python
  Python 3.10+ (the standard library is enough). Default: $env:BROKENDEV_PYTHON, else python on the PATH.
.PARAMETER Out
  The folder the cartridge goes to (default WideMess\testroms\bin). BrokenDev's project builds into its own output folder with it.
#>
param([string]$Wla = '', [string]$Workshop = '', [string]$Python = '', [string]$Out = '')
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $src '..\..')).Path
if (-not $Out) { $Out = Join-Path $repo 'WideMess\testroms\bin' }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path

if (-not $Wla) { $Wla = $env:BROKENDEV_WLA }
if (-not $Wla) { $Wla = Join-Path (Split-Path $repo -Parent) 'SuperVRUN_dev\snes-stub\toolchain\pvsneslib\devkitsnes\bin' }
if (-not (Test-Path -LiteralPath $Wla) -and $repo -match '\\\.claude\\worktrees\\') {   # from a .claude\worktrees checkout: beside the main checkout
    $Wla = Join-Path (Split-Path ($repo -replace '\\\.claude\\worktrees\\.*$', '') -Parent) 'SuperVRUN_dev\snes-stub\toolchain\pvsneslib\devkitsnes\bin'
}
$asm = Join-Path $Wla 'wla-65816.exe'; $asm700 = Join-Path $Wla 'wla-spc700.exe'; $link = Join-Path $Wla 'wlalink.exe'
foreach ($t in $asm, $asm700, $link) { if (-not (Test-Path -LiteralPath $t)) { throw "$t not found (pass -Wla or set BROKENDEV_WLA)" } }
if (-not $Workshop) { $Workshop = $env:BROKENNES_WORKSHOP }
if (-not $Workshop) { $Workshop = $env:BD_WORKSHOP }
if (-not $Workshop) {
    $Workshop = Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work') -Recurse -Filter 'BrokenNes.Workshop.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $Workshop -or -not (Test-Path -LiteralPath $Workshop)) { throw 'BrokenNes.Workshop.exe not found (pass -Workshop or set BROKENNES_WORKSHOP)' }
if (-not $Python) { $Python = $env:BROKENDEV_PYTHON }
if (-not $Python) { $Python = 'python' }

function Invoke-Tool([string]$exe, [string[]]$argList, [string]$cwd) {
    $o = Join-Path ([IO.Path]::GetTempPath()) ("wmstc_" + [guid]::NewGuid() + '.txt')
    $e = "$o.err"
    $p = Start-Process -FilePath $exe -ArgumentList $argList -WorkingDirectory $cwd -Wait -PassThru -NoNewWindow -RedirectStandardOutput $o -RedirectStandardError $e
    $text = (Get-Content -LiteralPath $o -Raw -ErrorAction SilentlyContinue) + (Get-Content -LiteralPath $e -Raw -ErrorAction SilentlyContinue)
    Remove-Item -LiteralPath $o, $e -ErrorAction SilentlyContinue
    if ($p.ExitCode -ne 0 -or $text -cmatch '(?m)^.*\bERROR\b.*$') { throw "$([IO.Path]::GetFileName($exe)) failed ($($p.ExitCode)): $text" }
    return $text
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('wms_testcart_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    # 1. the data (and the reference pictures)
    & $Python -I (Join-Path $src 'gen_testcart.py') $work $Out
    if ($LASTEXITCODE -ne 0) { throw 'gen_testcart.py failed' }
    # 2. the SPC700 program (a plain image from $0200)
    Copy-Item -LiteralPath (Join-Path $src 'tc_spc.asm') -Destination $work
    Invoke-Tool $asm700 @('-o', 'tc_spc.o', 'tc_spc.asm') $work | Out-Null
    Set-Content -LiteralPath (Join-Path $work 'tc_spc.link') -Value "[objects]`r`ntc_spc.o" -Encoding ascii
    Invoke-Tool $link @('-b', 'tc_spc.link', 'tc_spc.bin') $work | Out-Null
    Set-Content -LiteralPath (Join-Path $work 'tc_spcsize.inc') -Value ".DEFINE SPC_LEN $((Get-Item -LiteralPath (Join-Path $work 'tc_spc.bin')).Length)" -Encoding ascii
    # 3. the 65816 program
    Get-ChildItem -LiteralPath $src -File | Where-Object { $_.Extension -in '.asm', '.inc' -and $_.Name -ne 'tc_spc.asm' } | Copy-Item -Destination $work
    Invoke-Tool $asm @('-o', 'testcart.o', 'testcart.asm') $work | Out-Null
    Set-Content -LiteralPath (Join-Path $work 'testcart.link') -Value "[objects]`r`ntestcart.o" -Encoding ascii
    Invoke-Tool $link @('-S', '-r', 'testcart.link', 'testcart.sfc') $work | Out-Null
    $sfc = Join-Path $work 'testcart.sfc'
    if ((Get-Item -LiteralPath $sfc).Length -ne 512KB) { throw "the image is $((Get-Item -LiteralPath $sfc).Length) bytes, not 512 KB" }
    Copy-Item -LiteralPath $sfc -Destination (Join-Path $Out 'widemess-testcart.sfc') -Force
    if (Test-Path -LiteralPath (Join-Path $work 'testcart.sym')) { Copy-Item -LiteralPath (Join-Path $work 'testcart.sym') -Destination (Join-Path $Out 'widemess-testcart.sym') -Force }
    # 4. the .wms: map 2, no required features
    Invoke-Tool $Workshop @('--wms-pack', '--in', $sfc, '--out', (Join-Path $Out 'widemess-testcart.wms'), '--title', '"WideMess test cartridge"', '--features', '0', '--map', '2') $work
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
Get-ChildItem -LiteralPath $Out -Filter 'widemess-testcart*' | Select-Object Name, Length
