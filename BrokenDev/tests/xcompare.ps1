<#
.SYNOPSIS
  The cross-console comparison's own tests (Sega VRUN ports, S0: Workshop --timeflow compare / recheck, Workshop\Timeflow\TimeflowCompare.cs),
  run on the user's real games WITHOUT the ports: each game against itself, against a deliberately altered build, at the tolerance edges, with a
  mapped tick rate, and the re-check after a rebuild. Exit 0 = every check passed. Ends with one JSON line (checks, pass, fail).

.DESCRIPTION
  The games are only READ: VRUN: Vaults of Obsolete Code's sources (PROJECTS\VRUN_VOC_dev, Game Boy) are copied into the scratch folder and built
  there twice with the pinned GBDK (an unchanged rebuild, and an altered one: 15 materials make a life instead of 16, src\phys\voc_phys.c);
  VRUN NES and Super VRUN (SNES) are BrokenDev's published builds (BROKENDEV_HOME\out\vrun-nes, \out\supervrun), copied. Timelines go to a scratch
  BROKENDEV_TIMEFLOW, never the shared store.

  Checks (each with its control):
    voc-self            Vaults against itself: no drift, 20+ events matched
    voc-altered         Vaults against the altered build: a drift (negative control), kind 'time', on the first 'lives' event, every event before it matched
    voc-ticks-edge      the altered build with the tick tolerance = the drift's distance: that event passes; one less: it fails at the same event
    voc-position-edge   Vaults against itself with the port's x moved 1 cell: tolerance 1 passes, tolerance 0 fails with kind 'position'
    voc-rate-mapped     the original timed by its own 24 Hz tick counter, the port by its frames mapped to 24 ticks a second, both from the run's start
                        (origin mode=1): within 1 tick; a WRONG rate (25 ticks a second) drifts (control)
    voc-recheck         the port session recorded by voc-self re-checked on the unchanged rebuild (unchanged) and on the altered one (the first altered
                        frame, before the first lives event, with 'lives' among the variables that differ)
    nes-self, snes-self VRUN NES and Super VRUN against themselves: no drift (the comparison on the NES and the SNES)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkshopExe,
    [string]$VocDev = '',
    [string]$Scratch = (Join-Path $env:LOCALAPPDATA 'VRUN_Nes_Dev_work\xcompare_test'),
    [string]$Gbdk = 'P:\BrokenDev\env\toolchains\gbdk\4.5.0\gbdk',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
if (-not $VocDev) { $d = Split-Path -Parent $repo; while ($d -and -not (Test-Path (Join-Path $d 'VRUN_VOC_dev'))) { $p = Split-Path -Parent $d; if ($p -eq $d) { $d = ''; break }; $d = $p }; if ($d) { $VocDev = Join-Path $d 'VRUN_VOC_dev' } }
if (-not (Test-Path (Join-Path $VocDev 'src\phys\voc_phys.c'))) { throw "VRUN_VOC_dev not found (-VocDev)" }
$bdHome = if ($env:BROKENDEV_HOME) { $env:BROKENDEV_HOME } else { 'P:\BrokenDev\env' }
New-Item -ItemType Directory -Force $Scratch | Out-Null
$env:BROKENDEV_TIMEFLOW = Join-Path $Scratch 'timeflow'
if (Test-Path $env:BROKENDEV_TIMEFLOW) { Remove-Item -Recurse -Force $env:BROKENDEV_TIMEFLOW }
$results = New-Object System.Collections.Generic.List[object]
function Check([string]$Id, [bool]$Ok, [string]$Detail) { $results.Add([pscustomobject]@{ id = $Id; ok = $Ok; detail = $Detail }); Write-Host ("{0,-5} {1,-22} {2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Id, $Detail) }

function Ws([string[]]$ArgList) {
    $out = Join-Path $Scratch ('ws_' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
    $argv = $ArgList | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }
    $p = Start-Process -FilePath $WorkshopExe -ArgumentList $argv -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError "$out.err"
    $text = Get-Content -Raw $out
    $j = $null; try { $j = $text | ConvertFrom-Json } catch { }
    [pscustomobject]@{ Exit = $p.ExitCode; J = $j; Text = $text; Err = (Get-Content -Raw "$out.err") }
}
function Map([string]$Name, $Obj) { $p = Join-Path $Scratch $Name; $Obj | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $p; $p }

# ---- Vaults: the ROM as the user builds it (a copy of the sources, built here) and the altered build
$src = Join-Path $Scratch 'voc_src'
$same = Join-Path $Scratch 'voc_same'; $alt = Join-Path $Scratch 'voc_alt'
if (-not $SkipBuild -or -not (Test-Path "$alt\vrun_voc.gbc")) {
    if (Test-Path $src) { Remove-Item -Recurse -Force $src }
    New-Item -ItemType Directory -Force $src | Out-Null
    foreach ($x in 'src', 'tools', 'build.ps1') { Copy-Item -Recurse (Join-Path $VocDev $x) $src }
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'build.ps1') -Gbdk $Gbdk -Out $same *> (Join-Path $Scratch 'build_same.log')
    if ($LASTEXITCODE -ne 0) { throw "the Vaults build failed (see build_same.log)" }
    $f = Join-Path $src 'src\phys\voc_phys.c'
    $t = [IO.File]::ReadAllText($f)
    if (([regex]::Matches($t, 'if \(voc\.materials > 15\)')).Count -ne 1) { throw "the alteration's line is not in voc_phys.c once" }
    [IO.File]::WriteAllText($f, $t.Replace('if (voc.materials > 15)', 'if (voc.materials > 14)   /* xcompare: 15 materials make a life */'))
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src 'build.ps1') -Gbdk $Gbdk -Out $alt *> (Join-Path $Scratch 'build_alt.log')
    if ($LASTEXITCODE -ne 0) { throw "the altered Vaults build failed (see build_alt.log)" }
}
$rom = "$same\vrun_voc.gbc"; $noi = "$same\vrun_voc.noi"; $arom = "$alt\vrun_voc.gbc"; $anoi = "$alt\vrun_voc.noi"
Check 'voc-builds' ((Get-FileHash $rom).Hash -ne (Get-FileHash $arom).Hash) "unchanged build $((Get-FileHash $rom).Hash.Substring(0,12)), altered $((Get-FileHash $arom).Hash.Substring(0,12))"

# the session: A on the title (RUN), then a seeded wandering player, 6000 frames
$r = New-Object System.Random 7
$parts = @('60:A', '61:'); $fr = 200
while ($fr -lt 6000) {
    $dir = @('Right', 'Right', 'Left', 'Right', 'Down')[$r.Next(5)]
    $parts += "${fr}:$dir"
    if ($r.Next(3) -ne 0) { $j = $fr + 4 + $r.Next(12); $parts += "${j}:$dir+A"; $parts += "$($j + 3):$dir" }
    if ($r.Next(6) -eq 0) { $j = $fr + 20; $parts += "${j}:$dir+B"; $parts += "$($j + 2):$dir" }
    $fr += 30
}
$padScript = $parts -join ','
$vars = @(
    @{ name = 'mode'; kind = 'event'; original = '_game_mode' },
    @{ name = 'room'; kind = 'event'; original = 'le16 _voc + 448' },
    @{ name = 'lives'; kind = 'event'; original = 'le16 _voc + 503' },
    @{ name = 'clovers'; kind = 'event'; original = '[_voc + 505]' },
    @{ name = 'result'; kind = 'event'; original = '[_voc + 508]' },
    @{ name = 'x'; kind = 'position'; original = '[_voc + 459]' },
    @{ name = 'y'; kind = 'position'; original = '[_voc + 460]' })
$tickVar = @{ original = @{ tick = 'le16 _tick_count' }; port = @{ tick = 'le16 _tick_count' } }
$m0 = Map 'voc.json' @{ original = 'vrun-voc'; rate = $tickVar; tolerance = @{ ticks = 0; positions = 0 }; sampleEveryTicks = 24; variables = $vars }

$c = Ws @('--timeflow', 'compare', '--original-rom', $rom, '--input', $padScript, '--frames', '6000', '--original-sym', $noi, '--port', $rom, '--port-sym', $noi, '--map', $m0, '--json')
$orig = $c.J.original.timeline; $selfPort = $c.J.port.timeline
Check 'voc-self' ($c.Exit -eq 0 -and $null -eq $c.J.drift -and $c.J.eventsMatched -ge 20 -and $c.J.original.replayInSync) "exit $($c.Exit), $($c.J.verdict)"
$livesIdx = -1; $i = 0; foreach ($e in $c.J.eventsOriginal) { if ($e -match ' lives ' -and $livesIdx -lt 0) { $livesIdx = $i }; $i++ }

$c = Ws @('--timeflow', 'compare', '--original', $orig, '--original-sym', $noi, '--port', $arom, '--port-sym', $anoi, '--map', $m0, '--json')
$d = $c.J.drift
Check 'voc-altered' ($c.Exit -eq 1 -and $d.kind -eq 'time' -and $d.variable -eq 'lives' -and $d.event -eq $livesIdx -and $c.J.eventsMatched -eq $livesIdx) "exit $($c.Exit), first lives event #$livesIdx; $($c.J.verdict)"
Check 'voc-altered-opened' ($null -ne $d.opened.original.profile -and $null -ne $d.opened.port.profile -and $d.opened.port.profile.queries[0].total -ge 1) "both sides profiled before the drift; the port's writes to $($d.opened.port.watch): $($d.opened.port.profile.queries[0].total)"
$gap = [math]::Abs([double]$d.original.tick - [double]$d.port.tick)

$mT = Map 'voc_t.json' @{ rate = $tickVar; tolerance = @{ ticks = $gap; positions = 99 }; variables = $vars }
$c1 = Ws @('--timeflow', 'compare', '--original', $orig, '--original-sym', $noi, '--port', $arom, '--port-sym', $anoi, '--map', $mT, '--json', '--no-open')
$mT1 = Map 'voc_t1.json' @{ rate = $tickVar; tolerance = @{ ticks = ($gap - 1); positions = 99 }; variables = $vars }
$c2 = Ws @('--timeflow', 'compare', '--original', $orig, '--original-sym', $noi, '--port', $arom, '--port-sym', $anoi, '--map', $mT1, '--json', '--no-open')
Check 'voc-ticks-edge' (($null -eq $c1.J.drift -or $c1.J.eventsMatched -gt $livesIdx) -and $c2.J.drift.event -eq $livesIdx -and $c2.J.drift.kind -eq 'time') "tolerance $gap ticks: $($c1.J.verdict) | tolerance $($gap - 1): $($c2.J.verdict)"

$vx = @($vars | ForEach-Object { $h = $_.Clone(); if ($h.name -eq 'x') { $h['offset'] = @{ original = 0; port = 1 } }; $h })
$mP1 = Map 'voc_p1.json' @{ rate = $tickVar; tolerance = @{ ticks = 0; positions = 1 }; sampleEveryTicks = 24; variables = $vx }
$mP0 = Map 'voc_p0.json' @{ rate = $tickVar; tolerance = @{ ticks = 0; positions = 0 }; sampleEveryTicks = 24; variables = $vx }
$c1 = Ws @('--timeflow', 'compare', '--original', $orig, '--original-sym', $noi, '--port', $rom, '--port-sym', $noi, '--map', $mP1, '--json', '--no-open')
$c2 = Ws @('--timeflow', 'compare', '--original', $orig, '--original-sym', $noi, '--port', $rom, '--port-sym', $noi, '--map', $mP0, '--json', '--no-open')
Check 'voc-position-edge' ($c1.Exit -eq 0 -and $c2.Exit -eq 1 -and $c2.J.drift.kind -eq 'position' -and $c2.J.drift.variable -eq 'x') "x+1, tolerance 1: $($c1.J.verdict) | tolerance 0: $($c2.J.verdict)"

$rate = @{ original = @{ tick = 'le16 _tick_count'; origin = 'mode=1' }; port = @{ tickHz = 24; origin = 'mode=1' } }
$mR = Map 'voc_rate.json' @{ rate = $rate; tolerance = @{ ticks = 1; positions = 0 }; variables = $vars }
$c1 = Ws @('--timeflow', 'compare', '--original', $orig, '--original-sym', $noi, '--port', $rom, '--port-sym', $noi, '--map', $mR, '--json', '--no-open')
$rateW = @{ original = @{ tick = 'le16 _tick_count'; origin = 'mode=1' }; port = @{ tickHz = 25; origin = 'mode=1' } }
$mW = Map 'voc_rate_wrong.json' @{ rate = $rateW; tolerance = @{ ticks = 1; positions = 0 }; variables = $vars }
$c2 = Ws @('--timeflow', 'compare', '--original', $orig, '--original-sym', $noi, '--port', $rom, '--port-sym', $noi, '--map', $mW, '--json', '--no-open')
Check 'voc-rate-mapped' ($c1.Exit -eq 0 -and $c1.J.eventsMatched -ge 20 -and $c2.Exit -eq 1 -and $c2.J.drift.kind -eq 'time') "24 Hz by frames vs the game's tick: $($c1.J.verdict) | 25 Hz (wrong): $($c2.J.verdict)"

$k1 = Ws @('--timeflow', 'recheck', '--timeline', $selfPort, '--rom', $rom, '--sym-new', $noi, '--map', $m0, '--json')
$k2 = Ws @('--timeflow', 'recheck', '--timeline', $selfPort, '--rom', $arom, '--sym-new', $anoi, '--map', $m0, '--json')
$s2 = $k2.J.sessions[0]
$firstLivesFrame = [int](($c.J.eventsOriginal | Where-Object { $_ -match ' lives ' } | Select-Object -First 1) -replace '^#\d+ f(\d+) .*$', '$1')
Check 'voc-recheck-unchanged' ($k1.Exit -eq 0 -and -not $k1.J.changed -and $k1.J.sessions[0].sameRom) "$($k1.J.verdict)"
Check 'voc-recheck-changed' ($k2.Exit -eq 1 -and $s2.changed -and $s2.firstAlteredFrame -gt 239 -and $s2.firstAlteredFrame -le $firstLivesFrame -and $s2.variables.lives.differs) "first altered frame $($s2.firstAlteredFrame) ($($s2.what)), the first lives event at frame $firstLivesFrame; lives $($s2.variables.lives.recorded) -> $($s2.variables.lives.new)"

# ---- the NES and the SNES: the comparison against itself
$nes = Join-Path $Scratch 'nes'; New-Item -ItemType Directory -Force $nes | Out-Null
Copy-Item "$bdHome\out\vrun-nes\vrun_game.nes", "$bdHome\out\vrun-nes\vrun_game.mlb" $nes -Force
$mN = Map 'nes.json' @{ original = 'vrun-nes'; tolerance = @{ ticks = 0; positions = 0 }; sampleEveryTicks = 60
    variables = @(@{ name = 'app'; kind = 'event'; original = 'app_state' }, @{ name = 'slot'; kind = 'event'; original = 'map_slot' }, @{ name = 'cleared'; kind = 'event'; original = 'room_cleared' },
                  @{ name = 'x'; kind = 'position'; original = 'player_x@0' }, @{ name = 'y'; kind = 'position'; original = 'player_y@0' }) }
$c = Ws @('--timeflow', 'compare', '--original-rom', "$nes\vrun_game.nes", '--input', '120:Start,132:,400:Right,600:Right+B,700:Right,900:Up,1000:Right+B,1200:Down,1300:Right+A,1500:Left+B,1800:Right', '--frames', '2400',
          '--original-sym', "$nes\vrun_game.mlb", '--port', "$nes\vrun_game.nes", '--port-sym', "$nes\vrun_game.mlb", '--map', $mN, '--json')
Check 'nes-self' ($c.Exit -eq 0 -and $null -eq $c.J.drift -and $c.J.eventsMatched -ge 2) "exit $($c.Exit), $($c.J.verdict)$(if ($c.Err) { ' / ' + $c.Err.Trim() })"

$snes = Join-Path $Scratch 'snes'; New-Item -ItemType Directory -Force $snes | Out-Null
Copy-Item "$bdHome\out\supervrun\supervrun.sfc", "$bdHome\out\supervrun\supervrun.sym" $snes -Force
$mS = Map 'snes.json' @{ original = 'supervrun'; tolerance = @{ ticks = 0; positions = 0 }; sampleEveryTicks = 60
    variables = @(@{ name = 'state'; kind = 'event'; original = 'cur_state_seen' }, @{ name = 'hp'; kind = 'event'; original = 'player_hp' },
                  @{ name = 'x'; kind = 'position'; original = 'le16 player_x' }, @{ name = 'y'; kind = 'position'; original = 'le16 player_y' }) }
$c = Ws @('--timeflow', 'compare', '--original-rom', "$snes\supervrun.sfc", '--input', '300:Start,306:,520:Start,526:,1000:Right,1100:Right+Y,1150:Right,1400:Left+B,1500:Right', '--frames', '1800',
          '--original-sym', "$snes\supervrun.sym", '--port', "$snes\supervrun.sfc", '--port-sym', "$snes\supervrun.sym", '--map', $mS, '--json')
Check 'snes-self' ($c.Exit -eq 0 -and $null -eq $c.J.drift -and $c.J.eventsMatched -ge 2) "exit $($c.Exit), $($c.J.verdict)$(if ($c.Err) { ' / ' + $c.Err.Trim() })"

# ---- BrokenDev's verbs: 'timeflow compare <port project>' and 'timeflow recheck <port project>' on scratch projects (a scratch BROKENDEV_HOME and
# projects folder; each project's "build" copies a prebuilt ROM into its build folder, so a source switch is a rebuild)
$bdh = Join-Path $Scratch 'bdhome'; $pj = Join-Path $Scratch 'bdprojects'
foreach ($d in $bdh, $pj) { if (Test-Path $d) { Remove-Item -Recurse -Force $d }; New-Item -ItemType Directory -Force $d | Out-Null }
$copy = "param([string]`$From)`n`$here = Split-Path -Parent `$MyInvocation.MyCommand.Path`nNew-Item -ItemType Directory -Force (Join-Path `$here 'build') | Out-Null`nCopy-Item ((Join-Path `$here `$From) + '\*') (Join-Path `$here 'build') -Force`nGet-ChildItem (Join-Path `$here 'build') -File | ForEach-Object { `$_.LastWriteTime = Get-Date }`nexit 0`n"
function New-ScratchProject([string]$Id, [string]$Variant, $Comparison) {
    $srcDir = Join-Path $Scratch "bdsrc_$Id"; if (Test-Path $srcDir) { Remove-Item -Recurse -Force $srcDir }
    foreach ($v in 'unchanged', 'altered') { New-Item -ItemType Directory -Force (Join-Path $srcDir $v) | Out-Null }
    Copy-Item $rom, $noi (Join-Path $srcDir 'unchanged'); Copy-Item $arom, $anoi (Join-Path $srcDir 'altered')
    Set-Content -Encoding UTF8 (Join-Path $srcDir 'copy.ps1') $copy
    $m = [ordered]@{
        schema = 1; id = $Id; title = "xcompare scratch project $Id"; family = 'test'; kind = 'gb'; console = 'gbc'; description = 'BrokenDev\tests\xcompare.ps1'
        source = @{ folder = $srcDir }; requires = @{ tools = @('ps5'); projects = @(); inputs = @() }
        build = @{ steps = @(@{ name = 'copy'; shell = 'ps5'; cwd = '{src}'; file = '{src}/copy.ps1'; args = @('-From', $Variant); timeoutSec = 120 })
                   outputs = @(@{ id = 'rom'; path = '{src}/build/vrun_voc.gbc'; publishAs = 'vrun_voc.gbc'; kind = 'gbc' }, @{ id = 'noi'; path = '{src}/build/vrun_voc.noi'; publishAs = 'vrun_voc.noi'; kind = 'symbols' }) }
        verify = @{ scenarios = @(@{ id = 'play'; frames = 6000; input = $padScript; checkpoints = @(6000) }) }
        knownIssues = @()
    }
    if ($Comparison) { $m['comparison'] = $Comparison }
    New-Item -ItemType Directory -Force (Join-Path $pj $Id) | Out-Null
    $m | ConvertTo-Json -Depth 10 | Set-Content -Encoding UTF8 (Join-Path (Join-Path $pj $Id) 'project.json')
}
New-ScratchProject 'voc-orig' 'unchanged' $null
New-ScratchProject 'voc-port' 'altered' ([ordered]@{ original = 'voc-orig'; rate = $tickVar; tolerance = @{ ticks = 0; positions = 0 }; sampleEveryTicks = 24; variables = $vars })
$bd = Join-Path $repo 'BrokenDev\brokendev.ps1'
function Bd([string[]]$ArgList) {
    $o = & powershell -NoProfile -ExecutionPolicy Bypass -Command "`$env:BROKENDEV_HOME='$bdh'; `$env:BROKENDEV_PROJECTS='$pj'; `$env:BROKENDEV_WORKSHOP='$WorkshopExe'; `$env:BROKENDEV_TIMEFLOW='$($env:BROKENDEV_TIMEFLOW)'; & '$bd' $($ArgList -join ' '); exit `$LASTEXITCODE" 2>&1 | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Text = $o }
}
$b1 = Bd @('build', 'voc-orig'); $b2 = Bd @('build', 'voc-port')
$v = Bd @('timeflow', 'compare', 'voc-port', '-Scenario', 'play')
Check 'bd-compare-verb' ($b1.Exit -eq 0 -and $b2.Exit -eq 0 -and $v.Exit -eq 1 -and $v.Text -match 'DRIFT \(time\) after \d+ matching event' -and $v.Text -match 'opened port') "exit $($v.Exit): $((($v.Text -split "`n") | Where-Object { $_ -match 'DRIFT|no drift|recorded' }) -join ' / ')"
$k = Bd @('timeflow', 'recheck', 'voc-port')
Check 'bd-recheck-unchanged' ($k.Exit -eq 0 -and $k.Text -match 'changed none') "exit $($k.Exit): $((($k.Text -split "`n") | Where-Object { $_ -match 'changed|unchanged' } | Select-Object -First 2) -join ' / ')"
$pjm = Join-Path (Join-Path $pj 'voc-port') 'project.json'
(Get-Content -Raw $pjm).Replace('"altered"', '"unchanged"') | Set-Content -Encoding UTF8 $pjm
$k = Bd @('timeflow', 'recheck', 'voc-port')
Check 'bd-recheck-changed' ($k.Exit -eq 1 -and $k.Text -match 'first frame the change altered is \d+' -and $k.Text -match 'lives') "exit $($k.Exit): $((($k.Text -split "`n") | Where-Object { $_ -match 'altered' } | Select-Object -First 2) -join ' / ')"

$pass = @($results | Where-Object ok).Count; $fail = $results.Count - $pass
[pscustomobject]@{ test = 'xcompare'; checks = $results.Count; pass = $pass; fail = $fail } | ConvertTo-Json -Compress
if ($fail -gt 0) { exit 1 }
exit 0
