<#
.SYNOPSIS
  Super VRUN MUSIC TEST players (docs/projects/flp-music-pipeline.md section 8): an FL project (.flp) -> the game's own converters
  -> the game's music test player (every song of the game + this song in the FL TEST slot), for snes | wms | md.

.DESCRIPTION
  Contract (the thin wrappers build-supervrun-snes-mt.ps1 / -wms-mt.ps1 / -md-mt.ps1):
    -Flp x.flp -Out dir  ->  <Out>\<safe>.supervrun-<engine>-mt.sfc|.wms|.bin  and  <Out>\<safe>.supervrun-<engine>-mt.fence.txt
    (<safe> = the .flp's base name, chars not [A-Za-z0-9_-] -> '_'). Without -Flp: the placeholder song (<safe> = placeholder).
    Exit 0 ok; else nonzero and a line starting FAIL.
  1-3 as build-supervrun.ps1: FruityHost flp-backend (.flp -> sf2it's MNES MIDI + the back end's fence), sf2it.py (-> fltest.it),
  supervrun_fence.py (the converters' figures). 4: the game repository's music test target, in a scratch folder:
    snes / wms : SuperVRUN_dev snes-dev\supervrun\musictest\build-musictest.sh [--widemess] --fl fltest.it --out <work>
    md         : SuperVRUN_MD_dev musictest\build-musictest.ps1 -Fl fltest.it -Out <work>
  The game trees and their build folders are never written.
#>
param(
    [string]$Flp = '',
    [Parameter(Mandatory)] [ValidateSet('snes', 'wms', 'md')] [string]$Engine,
    [string]$Out = 'C:\BrokenNes-work\flp_out',
    [string]$Work = 'C:\BrokenNes-work\flp_supervrun_mt',
    [string]$Projects = 'C:\Users\philt\OneDrive\Documents\PROJECTS',
    [string]$FruityHost = '',
    [string]$Python = 'py -3.10',
    [string]$Sf2 = $env:MNES_SF2
)
$ErrorActionPreference = 'Stop'
$flpDir = Split-Path -Parent $PSScriptRoot           # BrokenDev\tools\music\flp
$id = "supervrun-$Engine"
$mtId = "$id-mt"
$snesTree = Join-Path $Projects 'SuperVRUN_dev\snes-dev\supervrun'
function Fail($m) { Write-Host "FAIL  $m"; exit 1 }
function Invoke-Py { $v = $Python -replace '^py\s+', ''; $out = $null | & py.exe $v -u -I @args; if ($LASTEXITCODE -ne 0) { $out | ForEach-Object { Write-Host "   $_" }; Fail "python $($args[0]) (exit $LASTEXITCODE)" }; $out }

if ($Flp) {
    if (-not (Test-Path $Flp)) { Fail "no such .flp: $Flp" }
    $name = [IO.Path]::GetFileNameWithoutExtension($Flp)
} else { $name = 'placeholder' }
$safe = ($name -replace '[^A-Za-z0-9_-]', '_')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$w = Join-Path $Work "$safe-$Engine-$stamp"
New-Item -ItemType Directory -Force $w, $Out | Out-Null
$fenceOut = Join-Path $Out "$safe.$mtId.fence.txt"
Write-Host "== $name -> $mtId (work $w)"

if ($Flp) {
    if (-not $FruityHost) {
        foreach ($c in @('C:\BrokenNes-work\mt_build_sv', "$env:BROKENNES_WORK\bn_build", 'C:\BrokenNes-work\flp2_build', 'C:\BrokenNes-work\bn_build', 'C:\BrokenNes-work\mt_build')) {
            $x = Join-Path $c 'bin\BrokenNes.FruityHost\release\BrokenNes.FruityHost.exe'; if (Test-Path $x) { $FruityHost = $x; break }
        }
    }
    if (-not $FruityHost -or -not (Test-Path $FruityHost)) { Fail 'FruityHost not built (dotnet build Plugin/BrokenNes.FruityHost -c Release --artifacts-path C:\BrokenNes-work\mt_build_sv) or pass -FruityHost' }
    if (-not $Sf2) { $Sf2 = @('C:\BrokenNes-tools\supervrun\MNES.sf2', 'C:\BrokenNes-handoff\2026-10-17-sega-r5\BrokenNes\Data\MNES.sf2') | Where-Object { Test-Path $_ } | Select-Object -First 1 }
    if (-not $Sf2) { Fail 'MNES.sf2 not found (set MNES_SF2 or -Sf2)' }
    & $FruityHost flp-backend $Flp $id $w | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'flp-backend' }
    $env:MNES_SF2 = $Sf2
    $it = Join-Path $w 'fltest.it'
    $sfargs = @((Join-Path $snesTree 'tools\music\sf2it.py'), (Join-Path $w "$name.mid"), '-o', $it, '--report', (Join-Path $w 'fltest.rep.json'))
    $pre = Join-Path $w "$name.presets.json"
    if (Test-Path $pre) { $sfargs += @('--presets', $pre) }
    Invoke-Py @sfargs | Out-Null
    $fargs = @((Join-Path $flpDir 'supervrun_fence.py'), (Join-Path $w "$name.fence.json"), $id, $it, (Join-Path $w 'fltest.rep.json'), '--snes', $snesTree, '--md', (Join-Path $Projects 'SuperVRUN_MD_dev'), '--dos', (Join-Path $Projects 'SuperVRUN_DOS_dev'))
    $report = Invoke-Py @fargs
    $report = @("music test player ($mtId): the song is the FL TEST slot (12), beside every song of the game") + @($report)
    $report | Set-Content $fenceOut
    $report | ForEach-Object { Write-Host "   $_" }
    $label = $name
} else {
    $it = ''
    @("music test player ($mtId): no .flp, the FL TEST slot plays the placeholder song",
      "${id}: 0 over the limit, 0 played differently, 0 inside (placeholder: no .flp given)") | Set-Content $fenceOut
    $label = '(NO .FLP)'
}

$build = Join-Path $w 'build'
switch ($Engine) {
    { $_ -in 'snes', 'wms' } {
        $bash = 'C:\Program Files\Git\bin\bash.exe'
        $sh = (Join-Path $snesTree 'musictest\build-musictest.sh') -replace '\\', '/'
        $args2 = @()
        if ($Engine -eq 'wms') { $args2 += '--widemess' }
        if ($it) { $args2 += @('--fl', "'$($it -replace '\\','/')'") }
        $args2 += @('--name', "'$($label -replace "'", '')'", '--out', "'$($build -replace '\\','/')'")
        $log = Join-Path $w 'build.log'
        & $bash -lc "'$sh' $($args2 -join ' ')" *> $log
        if ($LASTEXITCODE -ne 0) { Get-Content $log -Tail 25 | ForEach-Object { Write-Host "   $_" }; Fail "build-musictest.sh (log $log)" }
        $ext = if ($Engine -eq 'snes') { 'sfc' } else { 'wms' }
        $prod = Join-Path $build "svmusictest.$ext"
    }
    'md' {
        $ps = Join-Path $Projects 'SuperVRUN_MD_dev\musictest\build-musictest.ps1'
        $a = @('-NoProfile', '-File', $ps, '-Name', $label, '-Out', $build)
        if ($it) { $a += @('-Fl', $it) }
        $log = Join-Path $w 'build.log'
        & pwsh @a *> $log
        if ($LASTEXITCODE -ne 0) { Get-Content $log -Tail 25 | ForEach-Object { Write-Host "   $_" }; Fail "MD build-musictest.ps1 (log $log)" }
        $ext = 'bin'
        $prod = Join-Path $build 'svmusictest_md.bin'
    }
}
if (-not (Test-Path $prod)) { Fail "the build made no $prod" }
$dst = Join-Path $Out "$safe.$mtId.$ext"
Copy-Item $prod $dst -Force
$h = (Get-FileHash $dst -Algorithm SHA256).Hash.ToLowerInvariant()
"$h *$(Split-Path $dst -Leaf)" | Set-Content "$dst.sha256"
Write-Host "OK    $dst  sha256 $h"
Write-Host "      fence: $fenceOut"
exit 0
