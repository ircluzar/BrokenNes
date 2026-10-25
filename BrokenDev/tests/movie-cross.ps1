<#
.SYNOPSIS
  The cross-emulator check of BrokenNes movie exports (milestone D5): does BizHawk play the exported .bk2, and FCEUX the exported .fm2,
  exactly as they play the same input given frame by frame by BrokenDev's own compat scripts?

.DESCRIPTION
  For a built project (default vrun-nes): a BrokenNes movie of a pad script is recorded (Workshop --movie record) and exported
  (--movie export --bk2 / --fm2). Each emulator then runs twice with the same reads of the game's variables every frame (the project's
  compat reads): once with the script set by its compat Lua (joypad.set), once playing the exported movie (EmuHawk --movie, FCEUX
  -playmovie). The export is right when the two runs agree on every variable, every frame, and the pad the emulator's movie pressed
  ('note movie-pad') changes at the frames the BrokenNes movie's input changes.
  Negative control: the movie exported with its first press one frame late must NOT agree.
  The other emulators are cross-checks only (the user's decision): agents debug on BrokenNes. Needs the emulators installed
  (brokendev.ps1 emulators install) and the project built; what is missing is SKIPPED with the reason.
  Output folder: BROKENDEV_HOME\work\<project>\movie-cross. Exit 0 when every check that ran passed.
#>
[CmdletBinding()]
param([string]$Project = 'vrun-nes', [string]$WorkshopExe = '', [int]$Frames = 900, [string]$Script = '120:Start,132:,400:Right,600:Right+B,700:Right',
      [string[]]$Emu = @('bizhawk-neshawk', 'fceux'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bd = Split-Path -Parent $here
foreach ($l in 'Common', 'Tools', 'Archive', 'Toolchains', 'Manifest', 'Build', 'Verify', 'Emulators', 'Compat', 'Movies') { . (Join-Path $bd "lib\$l.ps1") }
if ($WorkshopExe) { $env:BROKENDEV_WORKSHOP = (Resolve-Path -LiteralPath $WorkshopExe).ProviderPath }

$script:checks = 0; $script:failed = @(); $script:skipped = 0
function Check { param([string]$Name, [bool]$Ok, [string]$Detail) $script:checks++; if ($Ok) { Write-Host "  PASS  ${Name}: $Detail" -ForegroundColor Green } else { $script:failed += $Name; Write-Host "  FAIL  ${Name}: $Detail" -ForegroundColor Red } }
function Skip { param([string]$Name, [string]$Why) $script:skipped++; Write-Host "  SKIP  ${Name}: $Why" -ForegroundColor Yellow }

$p = Get-BdProjectRomAndSymbols -Id $Project
$m = $p.Manifest
$console = [string]$m.console
$ctx = New-BdContext $m
$reads = @(Resolve-BdCompatReads -Compat $m.compat -Console $console -Ctx $ctx)
$work = Join-Path (Join-Path (Join-Path (Get-BdHome) 'work') $Project) 'movie-cross'
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null
$ext = Get-BdMovieExtension
$movie = Join-Path $work ("cross" + $ext)
$r = Invoke-BdWorkshopMovie -Arguments @('record', '--rom', $p.Rom, '--frames', [string]$Frames, '--input', $Script, '--out', $movie)
if ($r.ExitCode -ne 0) { throw "record failed: $($r.Output)" }
$late = Join-Path $work ("late" + $ext)
$mtext = [System.IO.File]::ReadAllText($movie)
$firstPress = [regex]::Match($mtext, '(?m)^i (\d+) ([A-Za-z][A-Za-z+]*) ')   # the first line that presses something (not 'i 0 - -')
if (-not $firstPress.Success) { throw 'the movie presses nothing: no negative control can be made from it' }
[System.IO.File]::WriteAllText($late, $mtext.Replace("i $($firstPress.Groups[1].Value) $($firstPress.Groups[2].Value) ", "i $([int]$firstPress.Groups[1].Value + 1) $($firstPress.Groups[2].Value) "))
# the frames where the BrokenNes movie's pad 1 changes, and what it then holds (BizHawk / FCEUX names differ: compared in lower case, sorted)
$changes = [ordered]@{}
foreach ($mm in [regex]::Matches($mtext, '(?m)^i (\d+) (\S+) ')) { $changes[[string]$mm.Groups[1].Value] = $mm.Groups[2].Value }
function Norm { param([string]$B) if ($B -eq '-' -or -not $B) { return '-' } ((@($B -split '\+' | ForEach-Object { $_.ToLowerInvariant() }) | Sort-Object) -join '+') }

$J = Read-BdEmulators
foreach ($id in $Emu) {
    $e = Get-BdEmulator -Id $id -J $J
    $fmt = if ([string]$e.adapter -eq 'bizhawk') { 'bk2' } elseif ([string]$e.adapter -eq 'fceux') { 'fm2' } else { '' }
    if (-not $fmt) { Skip $id "no movie format for the $($e.adapter) adapter"; continue }
    if (@($e.consoles) -notcontains $console) { Skip $id "does not run the $console"; continue }
    $exe = Get-BdEmuExe -Emu $e -J $J
    if (-not $exe) { Skip $id 'not installed (brokendev.ps1 emulators install)'; continue }
    $pkg = Get-BdEmuPackage -Id ([string]$e.package) -J $J
    $fn = Get-BdAdapterFunction -Adapter ([string]$e.adapter)
    $run = {
        param([string]$Name, [string]$PadScript, [string]$MovieFile)
        $out = Join-Path (Join-Path $work $id) $Name; $scr = Join-Path (Join-Path $work "$id-scratch") $Name
        New-Item -ItemType Directory -Force -Path $out, $scr | Out-Null
        $romCopy = Join-Path $scr (Split-Path -Leaf $p.Rom); Copy-Item -LiteralPath $p.Rom -Destination $romCopy -Force
        $runSpec = @{ Console = $console; Rom = $romCopy; Out = $out; SaveDir = (Join-Path $scr 'saves'); Frames = $Frames; Reads = $reads; Input = $PadScript; Shots = @() }
        if ($MovieFile) { $runSpec['Movie'] = $MovieFile }
        New-Item -ItemType Directory -Force -Path $runSpec.SaveDir | Out-Null
        $text = New-BdCompatSpecText -Run $runSpec
        $sp = Join-Path $out 'spec.txt'; [System.IO.File]::WriteAllText($sp, $text, (New-Object System.Text.UTF8Encoding($false)))
        $res = & $fn -Emu $e -Package $pkg -Exe $exe -SpecPath $sp -Spec (Read-BdCompatSpec -Text $text) -Scratch $scr -TimeoutSec 900
        $rp = Join-Path $out 'result.txt'
        if (-not (Test-Path -LiteralPath $rp)) { throw "$id $Name wrote no result (exit $($res.ExitCode))" }
        ConvertFrom-BdCompatResult -Text ([System.IO.File]::ReadAllText($rp))
    }
    $coreArg = if ($fmt -eq 'bk2' -and (Get-BdProp $e 'core' '')) { @('--core', [string]$e.core) } else { @() }
    $ex = Invoke-BdWorkshopMovie -Arguments (@('export', '--movie', $movie, '--rom', $p.Rom, "--$fmt", (Join-Path $work "cross.$fmt")) + $coreArg)
    $exl = Invoke-BdWorkshopMovie -Arguments (@('export', '--movie', $late, '--rom', $p.Rom, "--$fmt", (Join-Path $work "late.$fmt")) + $coreArg)
    if ($ex.ExitCode -ne 0 -or $exl.ExitCode -ne 0) { Check "$id export" $false "$($ex.Output) $($exl.Output)"; continue }
    Write-Host "== $id ($fmt)"
    $a = & $run 'script' $Script ''
    $b = & $run 'movie' '' (Join-Path $work "cross.$fmt")
    $c = & $run 'late' '' (Join-Path $work "late.$fmt")
    $endsOk = $a.End -and $a.End.State -eq 'done' -and $b.End -and $b.End.State -eq 'done'
    $diff = -1
    foreach ($f in $a.Frames) { if (-not $b.Values.ContainsKey($f) -or ((@($a.Values[$f]) -join ' ') -ne (@($b.Values[$f]) -join ' '))) { $diff = $f; break } }
    Check "$id plays the exported .$fmt exactly as the same input set frame by frame" ($endsOk -and $diff -lt 0 -and $a.Frames.Count -eq $Frames) $(if ($diff -lt 0) { "$($a.Frames.Count) frames, every variable equal ($(@($a.Reads) -join ', '))" } else { "first difference after $diff frames: script $(@($a.Values[$diff]) -join ' ') / movie $(@($b.Values[$diff]) -join ' ') (end: $($a.End.State) / $($b.End.State))" })
    $pads = [ordered]@{}
    foreach ($n in @($b.Notes)) { if ([string]$n -match '^movie-pad (\d+) (\S+)$') { $pads[[string]$Matches[1]] = Norm $Matches[2] } }
    $want = [ordered]@{}; foreach ($k in $changes.Keys) { $want[$k] = Norm $changes[$k] }
    $padOk = ($pads.Count -eq $want.Count) -and (@($pads.Keys | ForEach-Object { "$_ $($pads[$_])" }) -join ', ') -eq (@($want.Keys | ForEach-Object { "$_ $($want[$_])" }) -join ', ')
    Check "$id's movie presses the movie's buttons at the movie's frames" $padOk ("emulator: " + (@($pads.Keys | ForEach-Object { "$_ $($pads[$_])" }) -join ', ') + " | movie: " + (@($want.Keys | ForEach-Object { "$_ $($want[$_])" }) -join ', '))
    $cdiff = -1
    foreach ($f in $a.Frames) { if (-not $c.Values.ContainsKey($f) -or ((@($a.Values[$f]) -join ' ') -ne (@($c.Values[$f]) -join ' '))) { $cdiff = $f; break } }
    Check "NEGATIVE: $id with the movie's first press one frame late does not agree" ($cdiff -gt 0) "first difference after $cdiff frames"
    # BrokenNes against this emulator, by the game's variables (information: emulators boot differently)
    $dv = Invoke-BdWorkshopMovie -Arguments @('diverge', '--movie', $movie, '--rom', $p.Rom, '--against', (Join-Path (Join-Path $work $id) 'movie'), '--json')
    $dj = Get-BdJsonFromOutput $dv.Output
    if ($dj) { Write-Host "  INFO  BrokenNes against ${id}: $($dj.summary)" }
}
Write-Host ''
$ok = $script:failed.Count -eq 0
Write-Host ('{{"pass": {0}, "checks": {1}, "failed": {2}, "skipped": {3}}}' -f $ok.ToString().ToLowerInvariant(), $script:checks, $script:failed.Count, $script:skipped)
if ($ok) { exit 0 } else { exit 1 }
