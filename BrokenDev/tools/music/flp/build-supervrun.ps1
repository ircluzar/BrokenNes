<#
.SYNOPSIS
  The music pipeline's Super VRUN test build (docs/projects/flp-music-pipeline.md section 8): an FL project (.flp) -> the game's own converters -> a test build of the
  game with the song in place of the title music (ROSBIF, module 7: it plays at the title screen, seconds after power-on), plus the fence report.

.DESCRIPTION
  1. FruityHost flp-backend: .flp -> sf2it.py's MNES MIDI + the back end's fence (<name>.fence.json)
  2. sf2it.py (the SNES tree's converter, Python 3.10 with mido + numpy, MNES.sf2) -> rosbif.it + its report
  3. supervrun_fence.py: the converters' own figures added to the fence (sf2it report, check_modules.py, and per engine itconv.py's voices / oplconv.py's steals)
  4. a scratch copy of the game tree(s) with audio\rosbif.it replaced, built with the game's own build:
       snes / wms : SuperVRUN_dev snes-dev\supervrun: smconv (soundbank) + build.sh [--widemess]  -> supervrun.sfc / supervrun.wms
       md         : SuperVRUN_MD_dev build.ps1 (itconv.py reads the scratch SNES audio)            -> supervrun_md.bin
       dos / 95   : SuperVRUN_DOS_dev / SuperVRUN_95_dev build.ps1 -Snes <scratch>               -> SVRUN.EXE / the 95 exe
  The game trees are never written: everything happens in -Work (default C:\BrokenNes-work\flp_supervrun). Output: -Out\<name>.<engine>.<ext>, .fence.json, .fence.txt, .sha256.

.EXAMPLE
  pwsh BrokenDev/tools/music/flp/build-supervrun.ps1 -Flp "C:\Users\philt\Desktop\Reverse Coaster\overworld.flp" -Engine snes -Out C:\BrokenNes-work\flp_out
#>
param(
    [Parameter(Mandatory)] [string]$Flp,
    [Parameter(Mandatory)] [ValidateSet('snes', 'wms', 'md', 'dos', '95')] [string]$Engine,
    [string]$Out = 'C:\BrokenNes-work\flp_out',
    [string]$Work = 'C:\BrokenNes-work\flp_supervrun',
    [string]$Projects = 'C:\Users\philt\OneDrive\Documents\PROJECTS',
    [string]$FruityHost = '',
    [string]$Python = 'py -3.10',
    [string]$Sf2 = $env:MNES_SF2,
    [switch]$FenceOnly
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..\..\..')).Path
$id = @{ snes = 'supervrun-snes'; wms = 'supervrun-wms'; md = 'supervrun-md'; dos = 'supervrun-dos'; '95' = 'supervrun-95' }[$Engine]
$snesTree = Join-Path $Projects 'SuperVRUN_dev\snes-dev\supervrun'
$name = [IO.Path]::GetFileNameWithoutExtension($Flp)
$safe = ($name -replace '[^A-Za-z0-9_-]', '_')
function Fail($m) { Write-Host "FAIL  $m"; exit 1 }
function Py { $a = @($args | ForEach-Object { $_ }); $p = $Python -split ' '; & $p[0] @($p[1..($p.Count - 1)] + $a); if ($LASTEXITCODE -ne 0) { Fail "python $($a[0]) (exit $LASTEXITCODE)" } }

if (-not (Test-Path $Flp)) { Fail "no such .flp: $Flp" }
if (-not $FruityHost) {
    foreach ($c in @("$env:BROKENNES_WORK\bn_build", 'C:\BrokenNes-work\flp2_build', 'C:\BrokenNes-work\bn_build')) {
        $x = Join-Path $c 'bin\BrokenNes.FruityHost\release\BrokenNes.FruityHost.exe'; if (Test-Path $x) { $FruityHost = $x; break }
    }
}
if (-not $FruityHost -or -not (Test-Path $FruityHost)) { Fail 'FruityHost not built (dotnet build Plugin/BrokenNes.FruityHost -c Release --artifacts-path C:\BrokenNes-work\flp2_build) or pass -FruityHost' }
if (-not $Sf2) { $Sf2 = @('C:\BrokenNes-tools\supervrun\MNES.sf2', 'C:\BrokenNes-handoff\2026-10-17-sega-r5\BrokenNes\Data\MNES.sf2', (Join-Path $repo 'Data\MNES.sf2')) | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if (-not $Sf2) { Fail 'MNES.sf2 not found (set MNES_SF2 or -Sf2)' }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$w = Join-Path $Work "$safe-$Engine-$stamp"
New-Item -ItemType Directory -Force $w, $Out | Out-Null
Write-Host "== $name -> $id (work $w)"

# 1-3: back end, sf2it, fence
& $FruityHost flp-backend $Flp $id $w | Out-Null
if ($LASTEXITCODE -ne 0) { Fail 'flp-backend' }
$fence = Join-Path $w "$name.fence.json"
$env:MNES_SF2 = $Sf2
Py (Join-Path $snesTree 'tools\music\sf2it.py') (Join-Path $w "$name.mid") -o (Join-Path $w 'rosbif.it') --report (Join-Path $w 'rosbif.rep.json') | Out-Null
$fargs = @((Join-Path $here 'supervrun_fence.py'), $fence, $id, (Join-Path $w 'rosbif.it'), (Join-Path $w 'rosbif.rep.json'), '--snes', $snesTree, '--md', (Join-Path $Projects 'SuperVRUN_MD_dev'), '--dos', (Join-Path $Projects 'SuperVRUN_DOS_dev'))
$report = Py @fargs
$report | Set-Content (Join-Path $w "$name.fence.txt")
$report | ForEach-Object { Write-Host "   $_" }
Copy-Item $fence (Join-Path $Out "$safe.$Engine.fence.json") -Force
Copy-Item (Join-Path $w "$name.fence.txt") (Join-Path $Out "$safe.$Engine.fence.txt") -Force
if ($FenceOnly) { Write-Host "fence only: $Out\$safe.$Engine.fence.txt"; exit 0 }

# 4: the game, in a scratch copy, with the song as module 7 (ROSBIF = the title music)
$snesCopy = Join-Path $w 'supervrun'
robocopy $snesTree $snesCopy /E /NFL /NDL /NJH /NJS /NP /XD build build-wms .git __pycache__ | Out-Null
if ($LASTEXITCODE -ge 8) { Fail "robocopy of the SNES tree ($LASTEXITCODE)" }
Copy-Item (Join-Path $w 'rosbif.it') (Join-Path $snesCopy 'audio\rosbif.it') -Force

function Publish($file, $ext) {
    if (-not (Test-Path $file)) { Fail "the build made no $file" }
    $dst = Join-Path $Out "$safe.$Engine.$ext"
    Copy-Item $file $dst -Force
    $h = (Get-FileHash $dst -Algorithm SHA256).Hash.ToLowerInvariant()
    "$h *$(Split-Path $dst -Leaf)" | Set-Content "$dst.sha256"
    Write-Host "OK    $dst  sha256 $h"
}

switch ($Engine) {
    { $_ -in 'snes', 'wms' } {
        $tools = if ($env:BROKENNES_TOOLS) { $env:BROKENNES_TOOLS } else { 'C:\BrokenNes-tools' }
        $smconv = Join-Path $tools 'supervrun\snes-stub\toolchain\pvsneslib\devkitsnes\tools\smconv.exe'
        if (-not (Test-Path $smconv)) { Fail "smconv not found: $smconv" }
        $mods = 'sfx think boss baropen baropencrpt chicagovoices puzzle rosbif satnkrt sundaydrive vectorplane level2'.Split(' ') | ForEach-Object { "$_.it" }
        Push-Location (Join-Path $snesCopy 'audio')
        & $smconv -s -V -f -b 12 -o soundbank @mods | Out-Null
        $rc = $LASTEXITCODE; Pop-Location
        if ($rc -ne 0) { Fail "smconv ($rc)" }
        Copy-Item (Join-Path $snesCopy 'audio\soundbank.h') (Join-Path $snesCopy 'src\soundbank.h') -Force
        $bash = 'C:\Program Files\Git\bin\bash.exe'
        $sh = ($snesCopy -replace '\\', '/') + '/build.sh'
        $log = Join-Path $w 'build.log'
        & $bash -lc "cd '$(($snesCopy -replace '\\','/'))' && ./build.sh $(if ($Engine -eq 'wms') { '--widemess' })" *> $log
        if ($LASTEXITCODE -ne 0) { Get-Content $log -Tail 25 | ForEach-Object { Write-Host "   $_" }; Fail "build.sh (log $log)" }
        if ($Engine -eq 'snes') { Publish (Join-Path $snesCopy 'build\supervrun.sfc') 'sfc' } else { Publish (Join-Path $snesCopy 'build-wms\supervrun.wms') 'wms' }
    }
    'md' {
        $mdCopy = Join-Path $w 'md'
        robocopy (Join-Path $Projects 'SuperVRUN_MD_dev') $mdCopy /E /NFL /NDL /NJH /NJS /NP /XD build .git out __pycache__ | Out-Null
        if ($LASTEXITCODE -ge 8) { Fail "robocopy of the MD tree ($LASTEXITCODE)" }
        $bp = Join-Path $mdCopy 'build.ps1'
        (Get-Content $bp -Raw).Replace($snesTree, $snesCopy) | Set-Content $bp -NoNewline
        $log = Join-Path $w 'build.log'
        & pwsh -NoProfile -File $bp *> $log
        if ($LASTEXITCODE -ne 0) { Get-Content $log -Tail 25 | ForEach-Object { Write-Host "   $_" }; Fail "MD build.ps1 (log $log)" }
        Publish (Join-Path $mdCopy 'build\supervrun_md.bin') 'bin'
    }
    { $_ -in 'dos', '95' } {
        $src = Join-Path $Projects $(if ($Engine -eq 'dos') { 'SuperVRUN_DOS_dev' } else { 'SuperVRUN_95_dev' })
        $dCopy = Join-Path $w $Engine
        robocopy $src $dCopy /E /NFL /NDL /NJH /NJS /NP /XD build .git __pycache__ | Out-Null
        if ($LASTEXITCODE -ge 8) { Fail "robocopy of $src ($LASTEXITCODE)" }
        $log = Join-Path $w 'build.log'
        & pwsh -NoProfile -File (Join-Path $dCopy 'build.ps1') -Snes $snesCopy *> $log
        if ($LASTEXITCODE -ne 0) { Get-Content $log -Tail 25 | ForEach-Object { Write-Host "   $_" }; Fail "build.ps1 (log $log)" }
        $exe = Get-ChildItem (Join-Path $dCopy 'build') -Filter *.EXE | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        Publish $exe.FullName 'exe'
    }
}
