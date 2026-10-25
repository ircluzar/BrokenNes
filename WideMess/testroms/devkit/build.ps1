<#
.SYNOPSIS
  Builds milestone 5's test ROMs (the devkit) into WideMess\testroms\bin, with BrokenDev's WideMess devkit (BrokenDev\widemess):
    devkit_template.wms            the template of 'brokendev new widemess' (3 layers, a sprite, a song, an effect, fast mode, far RAM)
    devkit_template_nounlock.wms   the negative control: the same game built with -D WMS_NOUNLOCK (no unlock; it writes everything anyway)
    devkit_sample.wms (+ .sym)     the sample game, Wide Blaster (WideMess\sample)
    devkit_sample_nounlock.wms     its negative control
    devkit_sample_snesspeed.wms    the sample with fast mode off (-D SAMPLE_SNES_SPEED): the control for "fast mode keeps 60 frames a second"
    devkit_sample_noclear.wms (+ .sym)  the sample without the start-up's work-RAM clear (-D WMS_NOCLEAR): the old start-up, byte for byte
  Workshop --wms-test checks them (WideMessCli.Devkit.cs, section M5); Workshop --check-test (group wms) checks the sample and its
  no-clear control with the hardware checks (the start-up clear: no work RAM read before written).
.PARAMETER Pvsneslib
  PVSnesLib's folder. Default: wmsbuild.ps1's (BROKENDEV_PVSNESLIB, else the Super VRUN workspace beside the repository).
.PARAMETER Workshop
  BrokenNes.Workshop.exe (for --wms-pack). Default: $env:BROKENNES_WORKSHOP, $env:BROKENDEV_WORKSHOP.
#>
param([string]$Pvsneslib = '', [string]$Workshop = '')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$bin = Join-Path $repo 'WideMess\testroms\bin'
$wmsbuild = Join-Path $repo 'BrokenDev\widemess\tools\wmsbuild.ps1'
if (-not $Workshop) { $Workshop = $env:BROKENNES_WORKSHOP }
if (-not $Workshop) { $Workshop = $env:BROKENDEV_WORKSHOP }
if (-not $Workshop -or -not (Test-Path -LiteralPath $Workshop)) { throw 'BrokenNes.Workshop.exe not found (pass -Workshop or set BROKENNES_WORKSHOP)' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('wms_devkit_' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Build([string]$project, [string]$out, [string[]]$define = @()) {
    $a = @{ Project = $project; Out = $out; Workshop = $Workshop }
    if ($Pvsneslib) { $a['Pvsneslib'] = $Pvsneslib }
    if ($define.Count) { $a['Define'] = $define }
    & $wmsbuild @a | Out-Null
}

try {
    # the template, as 'brokendev new widemess devkit-template' fills it in
    $tpl = Join-Path $work 'template'
    Copy-Item -LiteralPath (Join-Path $repo 'BrokenDev\widemess\template') -Destination $tpl -Recurse
    foreach ($f in @(Get-ChildItem -LiteralPath $tpl -Recurse -File -Include *.c, *.h, *.asm, *.json)) {
        $t = [IO.File]::ReadAllText($f.FullName).Replace('{{NAME}}', 'devkit_template').Replace('{{TITLE}}', 'DEVKIT TEMPLATE')
        [IO.File]::WriteAllText($f.FullName, $t)
    }
    Build $tpl (Join-Path $work 't')
    Build $tpl (Join-Path $work 'tn') @('WMS_NOUNLOCK')
    Copy-Item (Join-Path $work 't\devkit_template.wms') (Join-Path $bin 'devkit_template.wms') -Force
    Copy-Item (Join-Path $work 'tn\devkit_template.wms') (Join-Path $bin 'devkit_template_nounlock.wms') -Force

    # the sample (built out of its folder: its own build\ is untouched)
    $sample = Join-Path $repo 'WideMess\sample'
    Build $sample (Join-Path $work 's')
    Build $sample (Join-Path $work 'sn') @('WMS_NOUNLOCK')
    Build $sample (Join-Path $work 'ss') @('SAMPLE_SNES_SPEED')
    Build $sample (Join-Path $work 'sc') @('WMS_NOCLEAR')
    Copy-Item (Join-Path $work 's\widemess_sample.wms') (Join-Path $bin 'devkit_sample.wms') -Force
    Copy-Item (Join-Path $work 's\widemess_sample.sym') (Join-Path $bin 'devkit_sample.sym') -Force
    Copy-Item (Join-Path $work 'sn\widemess_sample.wms') (Join-Path $bin 'devkit_sample_nounlock.wms') -Force
    Copy-Item (Join-Path $work 'ss\widemess_sample.wms') (Join-Path $bin 'devkit_sample_snesspeed.wms') -Force
    Copy-Item (Join-Path $work 'sc\widemess_sample.wms') (Join-Path $bin 'devkit_sample_noclear.wms') -Force
    Copy-Item (Join-Path $work 'sc\widemess_sample.sym') (Join-Path $bin 'devkit_sample_noclear.sym') -Force
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
Get-ChildItem $bin -Filter 'devkit_*' | Select-Object Name, Length
