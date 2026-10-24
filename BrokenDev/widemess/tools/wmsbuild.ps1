<#
.SYNOPSIS
  Builds a WideMess game (BrokenDev's WideMess devkit): PVSnesLib + the WideMess library on the WideMess map, packed as a .wms.

.DESCRIPTION
  A WideMess project is a folder with a wms.json (name, title, ROM size, features, assets), C sources and 65816 assembly in src\,
  and its assets (PNG, WAV, MIDI). This script, run by 'brokendev.ps1 build <project>' (or by hand), does:
    1. the converters: wmsgfx.py (PNG -> 8-bpp layers, 4/8-bpp sprites, one 1,024-colour palette) and wmssnd.py (WAV -> 16-bit PCM
       or BRR, MIDI -> the sound driver's songs), into build\gen;
    2. hdr.asm for the WideMess map (lib\wmshdr.asm.in: PVSnesLib's HiROM memory map plus the far work-RAM slot);
    3. 816-tcc + 816-opt + wla-65816 for every src\*.c and the library (lib\widemess.c, lib\wmssound.c), wla-65816 for src\*.asm,
       lib\wmsfar.asm and the generated assets, wla-spc700 for the sound driver (driver\wmsdrv.asm);
    4. wlalink against PVSnesLib's HiROM libraries;
    5. Workshop --wms-pack --map 2: build\<name>.wms (plus build\<name>.sym, build\<name>.sfc the bare image).
  It never writes outside the project's build folder. PVSnesLib is only read.

.PARAMETER Project
  The project folder (holds wms.json).
.PARAMETER Pvsneslib
  PVSnesLib's folder (holds devkitsnes and pvsneslib). Default: $env:BROKENDEV_PVSNESLIB, else the SuperVRUN_dev toolchain found beside the repository.
.PARAMETER Workshop
  BrokenNes.Workshop.exe, for --wms-pack. Default: $env:BD_WORKSHOP, $env:BROKENDEV_WORKSHOP.
.PARAMETER Python
  Python 3.10+ with Pillow and NumPy. Default: $env:BROKENDEV_PYTHON, else the first one BrokenDev finds.
.PARAMETER Define
  Extra -D names for the C and assembly sources (e.g. WMS_NOUNLOCK, the negative control: no unlock).
.PARAMETER Out
  The build folder (default <Project>\build). With -Define, use another folder so the normal build is not overwritten.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Project,
    [string]$Pvsneslib = '',
    [string]$Workshop = '',
    [string]$Python = '',
    [string[]]$Define = @(),
    [string]$Out = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$wmsRoot = Split-Path -Parent $PSScriptRoot                        # BrokenDev\widemess
. (Join-Path (Split-Path -Parent $wmsRoot) 'lib\Common.ps1')        # Invoke-BdProcess and friends
. (Join-Path (Split-Path -Parent $wmsRoot) 'lib\Tools.ps1')         # Find-BdPython
$libDir = Join-Path $wmsRoot 'lib'
$drvDir = Join-Path $wmsRoot 'driver'

function Fail([string]$m) { throw "wmsbuild: $m" }

# ---- inputs
$Project = (Resolve-Path -LiteralPath $Project).Path
$cfgPath = Join-Path $Project 'wms.json'
if (-not (Test-Path -LiteralPath $cfgPath)) { Fail "no wms.json in $Project" }
$cfg = Get-Content -LiteralPath $cfgPath -Raw | ConvertFrom-Json
$name = [string](Get-BdProp $cfg 'name' '')
if ($name -notmatch '^[a-z][a-z0-9_]{0,30}$') { Fail "wms.json: name '$name' must be lower-case letters, digits and _ (it names the ROM)" }
$title = [string](Get-BdProp $cfg 'title' $name.ToUpperInvariant())
$romBanks = [int](Get-BdProp $cfg 'romBanks' 4)
if ($romBanks -lt 2 -or $romBanks -gt 64) { Fail "wms.json: romBanks $romBanks must be 2-64 (64 KB banks: 128 KB to 4 MB; the WideMess map takes 64 KB to 4 MB, PVSnesLib's libraries need 2 banks)" }
$fastRom = [bool](Get-BdProp $cfg 'fastRom' $true)
$sram = [int](Get-BdProp $cfg 'sramBytes' 0)
$battery = [bool](Get-BdProp $cfg 'battery' $false)
$featNames = @{ picture = 0x01; sprites = 0x02; sound = 0x04; fastcpu = 0x08; bigwram = 0x10; fastdma = 0x20 }
$features = 0
foreach ($f in @(Get-BdProp $cfg 'features' @('picture'))) {
    $k = ([string]$f).ToLowerInvariant()
    if (-not $featNames.ContainsKey($k)) { Fail "wms.json: unknown feature '$f' (known: $($featNames.Keys -join ', '))" }
    $features = $features -bor $featNames[$k]
}

if (-not $Pvsneslib) { $Pvsneslib = $env:BROKENDEV_PVSNESLIB }
if (-not $Pvsneslib) {
    # the user's PVSnesLib, shipped in the Super VRUN workspace beside the repository (a worktree looks beside the main checkout)
    $repo = Get-BdRepoRoot
    $cands = @((Join-Path (Split-Path $repo -Parent) 'SuperVRUN_dev\snes-stub\toolchain\pvsneslib'))
    if ($repo -match '^(.*?)\\[^\\]+\\\.claude\\worktrees\\') { $cands += (Join-Path $Matches[1] 'SuperVRUN_dev\snes-stub\toolchain\pvsneslib') }
    foreach ($c in $cands) { if (Test-Path -LiteralPath (Join-Path $c 'devkitsnes\bin\816-tcc.exe')) { $Pvsneslib = $c; break } }
}
if (-not $Pvsneslib -or -not (Test-Path -LiteralPath (Join-Path $Pvsneslib 'devkitsnes\bin\816-tcc.exe'))) { Fail "PVSnesLib not found (pass -Pvsneslib or set BROKENDEV_PVSNESLIB; looked for devkitsnes\bin\816-tcc.exe)" }
$Pvsneslib = (Resolve-Path -LiteralPath $Pvsneslib).Path
$bin = Join-Path $Pvsneslib 'devkitsnes\bin'; $tools = Join-Path $Pvsneslib 'devkitsnes\tools'
$tcc = Join-Path $bin '816-tcc.exe'; $opt = Join-Path $tools '816-opt.exe'; $as = Join-Path $bin 'wla-65816.exe'; $as700 = Join-Path $bin 'wla-spc700.exe'; $ld = Join-Path $bin 'wlalink.exe'
foreach ($t in $tcc, $opt, $as, $as700, $ld) { if (-not (Test-Path -LiteralPath $t)) { Fail "missing $t" } }
$libObjDir = Join-Path $Pvsneslib ("pvsneslib\lib\HiROM_" + $(if ($fastRom) { 'FastROM' } else { 'SlowROM' }))
if (-not (Test-Path -LiteralPath (Join-Path $libObjDir 'crt0_snes.obj'))) { Fail "missing PVSnesLib's HiROM libraries in $libObjDir" }

if (-not $Workshop) { $Workshop = $env:BD_WORKSHOP }
if (-not $Workshop) { $Workshop = $env:BROKENDEV_WORKSHOP }
if ($Workshop) { $Workshop = $Workshop.Replace('/', '\') }
if (-not $Workshop -or -not (Test-Path -LiteralPath $Workshop)) { Fail "BrokenNes.Workshop.exe not found (pass -Workshop, or run brokendev.ps1 build-workshop): it packs the .wms" }
if (-not $Python) { $Python = $env:BROKENDEV_PYTHON }
if (-not $Python) { $found = Find-BdPython; if ($found) { $Python = $found.Path } }
if (-not $Python) { Fail 'no Python 3.10+ with Pillow and NumPy found (pass -Python or set BROKENDEV_PYTHON)' }

if (-not $Out) { $Out = Join-Path $Project 'build' }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path -LiteralPath $Out).Path
$gen = Join-Path $Out 'gen'
New-Item -ItemType Directory -Force -Path $gen | Out-Null
$logDir = Join-Path $Out 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$step = 0
function Run([string]$what, [string]$exe, [string[]]$a, [string]$cwd = $Out) {
    $script:step++
    $log = Join-Path $logDir ('{0:D3}-{1}.log' -f $script:step, ($what -replace '[^A-Za-z0-9_\.\-]', '_'))
    $r = Invoke-BdProcess -FilePath $exe -Arguments $a -WorkingDirectory $cwd -TimeoutSec 600 -LogFile $log
    if ($r.TimedOut -or $r.ExitCode -ne 0) {
        # the lines that say what went wrong (the linker lists every section it discards first), else the tail
        $bad = @(($r.Output -split "`r?`n") | Where-Object { $_ -cmatch 'ERROR|FIX_REFERENCES|error|Error' -and $_ -notmatch '^DISCARD' } | Select-Object -First 12)
        $why = if ($bad.Count) { $bad -join "`n" } else { Select-BdTail $r.Output 20 }
        Fail "$what failed (exit $($r.ExitCode)); log $log`n$why"
    }
    # wla-65816 and wlalink can print an error and still exit 0 in some versions: an 'error' line is a failure
    if ($r.Output -cmatch '(?m)^.*(\bERROR\b|FIX_REFERENCES).*$') { Fail "$what reported: $($Matches[0])`nlog $log" }
    $r
}
Write-Host "wmsbuild: $name ('$title'), $romBanks x 64 KB, $(if ($fastRom) { 'FastROM' } else { 'SlowROM' }), features 0x$('{0:X2}' -f $features)$(if ($Define.Count) { ', -D ' + ($Define -join ' -D ') })"
Write-Host "  PVSnesLib $Pvsneslib"

# ---- 1. the sound driver (an SPC700 program, a plain image from $0200), then the converters (the assets of wms.json)
$objs = New-Object System.Collections.Generic.List[string]
Get-ChildItem -LiteralPath $gen -File -ErrorAction SilentlyContinue | Remove-Item -Force    # nothing stale from an earlier build
$soundOn = $null -ne (Get-BdProp $cfg 'sound' $null)
if ($soundOn -and ($features -band 0x04) -eq 0) { Fail "wms.json has a 'sound' section but its features lack 'sound' (the .wms header must ask for the sound chip)" }
if ($soundOn) {
    Copy-Item -LiteralPath (Join-Path $drvDir 'wmsdrv.asm') -Destination (Join-Path $Out 'wmsdrv.asm') -Force
    [void](Run 'spc wmsdrv' $as700 @('-o', 'wmsdrv.o', 'wmsdrv.asm'))
    [System.IO.File]::WriteAllText((Join-Path $Out 'wmsdrv.link'), "[objects]`r`nwmsdrv.o`r`n")
    [void](Run 'link wmsdrv' $ld @('-b', 'wmsdrv.link', (Join-Path $gen 'wmsdrv.bin')))
}
foreach ($conv in @(@{ Key = 'graphics'; Script = 'wmsgfx.py' }, @{ Key = 'sound'; Script = 'wmssnd.py' })) {
    if ($null -eq (Get-BdProp $cfg $conv.Key $null)) { continue }
    $sargs = @('-I', (Join-Path $PSScriptRoot $conv.Script), 'build', $cfgPath, $gen)
    if ($conv.Key -eq 'sound') { $sargs += @('--driver', (Join-Path $gen 'wmsdrv.bin'), '--snesbrr', (Join-Path $tools 'snesbrr.exe')) }
    $r = Run $conv.Script $Python $sargs $Project
    foreach ($line in ($r.Output -split "`r?`n")) { if ($line -match '^wms(gfx|snd):') { Write-Host "  $line" } }
}
$genAsm = @(Get-ChildItem -LiteralPath $gen -Filter '*.asm' -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { $_.FullName })

# ---- 2. hdr.asm
$romSizeCode = 5; while ((1 -shl $romSizeCode) * 1024 -lt $romBanks * 65536) { $romSizeCode++ }
$t21 = ($title.ToUpperInvariant() -replace '[^ -~]', ' ')
if ($t21.Length -gt 21) { $t21 = $t21.Substring(0, 21) }
$t21 = $t21.PadRight(21)
$hdr = (Get-Content -LiteralPath (Join-Path $libDir 'wmshdr.asm.in') -Raw)
$hdr = $hdr.Replace('@FASTROMDEF@', $(if ($fastRom) { '.DEFINE FASTROM 1' } else { '' })).Replace('@ROMSPEED@', $(if ($fastRom) { 'FASTROM' } else { 'SLOWROM' }))
$hdr = $hdr.Replace('@ROMBANKS@', [string]$romBanks).Replace('@ROMTITLE@', $t21).Replace('@ROMSIZE@', ('{0:X2}' -f $romSizeCode))
foreach ($d in $Define) { $hdr = ".DEFINE $d 1`r`n" + $hdr }
[System.IO.File]::WriteAllText((Join-Path $Out 'hdr.asm'), $hdr, (New-Object System.Text.UTF8Encoding($false)))
[void](Run 'hdr' $as @('-d', '-s', '-x', '-o', 'hdr.obj', 'hdr.asm'))
$objs.Add('hdr.obj')

# ---- 3. C and assembly
$incs = @("-I$(Join-Path $Pvsneslib 'pvsneslib\include')", "-I$(Join-Path $Pvsneslib 'devkitsnes\include')", "-I$libDir", "-I$(Join-Path $Project 'src')", "-I$gen")
$cflags = @('-Wall', '-c', '-H') + $(if ($fastRom) { @('-F') } else { @() }) + @($Define | ForEach-Object { "-D$_" })
$cfiles = @()
$cfiles += @(Get-ChildItem -LiteralPath (Join-Path $Project 'src') -Filter '*.c' -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { $_.FullName })
$cfiles += (Join-Path $libDir 'widemess.c')
if ($soundOn) { $cfiles += (Join-Path $libDir 'wmssound.c') }
$seen = @{}
foreach ($c in $cfiles) {
    $base = [System.IO.Path]::GetFileNameWithoutExtension($c)
    if ($seen.ContainsKey($base)) { Fail "two C files are called $base.c ($c and $($seen[$base])): the build folder holds one $base.asm" }
    $seen[$base] = $c
    [void](Run "tcc $base" $tcc ($incs + $cflags + @($c, '-o', "$base.ps")))
    [void](Run "opt $base" $opt @('-i', "$base.ps", '-o', "$base.asm"))
    [void](Run "as $base" $as @('-d', '-s', '-x', '-o', "$base.obj", "$base.asm"))
    $objs.Add("$base.obj")
}
$sfiles = @()
$sfiles += @(Get-ChildItem -LiteralPath (Join-Path $Project 'src') -Filter '*.asm' -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { $_.FullName })
$sfiles += (Join-Path $libDir 'wmsfar.asm')
$sfiles += (Join-Path $libDir 'wmslib.asm')
$sfiles += $genAsm
foreach ($s in $sfiles) {
    $base = [System.IO.Path]::GetFileNameWithoutExtension($s) + '_s'
    if ($seen.ContainsKey($base)) { Fail "two assembly files are called $base" }
    $seen[$base] = $s
    $dst = Join-Path $Out "$base.asm"
    if ([System.IO.Path]::GetFullPath($s) -ne $dst) { Copy-Item -LiteralPath $s -Destination $dst -Force }
    [void](Run "as $base" $as @('-d', '-s', '-x', '-o', "$base.obj", "$base.asm"))
    $objs.Add("$base.obj")
}

# ---- 4. link
$sfc = Join-Path $Out "$name.sfc"; $sym = Join-Path $Out "$name.sym"
Remove-Item -LiteralPath $sfc, $sym -ErrorAction SilentlyContinue
$link = New-Object System.Text.StringBuilder
[void]$link.AppendLine('[objects]')
foreach ($o in $objs) { [void]$link.AppendLine($o) }
foreach ($o in @(Get-ChildItem -LiteralPath $libObjDir -Filter '*.obj' -File | Sort-Object Name)) { [void]$link.AppendLine($o.FullName) }
[System.IO.File]::WriteAllText((Join-Path $Out 'linkfile'), $link.ToString())
[void](Run 'link' $ld @('-d', '-s', '-v', '-A', '-c', '-L', $libObjDir, 'linkfile', "$name.sfc"))
if (-not (Test-Path -LiteralPath $sfc)) { Fail "the linker wrote no $sfc" }
if (-not (Test-Path -LiteralPath $sym) -or (Get-Item -LiteralPath $sym).Length -eq 0) { Fail "the linker wrote an empty $name.sym: a reference it could not resolve (see the link log)" }
$len = (Get-Item -LiteralPath $sfc).Length
if ($len -ne $romBanks * 65536) { Fail "the image is $len bytes, not $romBanks x 64 KB" }

# ---- 5. the .wms
$wms = Join-Path $Out "$name.wms"
$pack = @('--wms-pack', '--in', $sfc, '--out', $wms, '--map', '2', '--title', $title, '--features', [string]$features)
if ($sram -gt 0) { $pack += @('--sram', [string]$sram) }
if ($battery) { $pack += '--battery' }
$r = Run 'wms-pack' $Workshop $pack
Write-Host ("  " + (($r.Output -split "`r?`n") | Where-Object { $_ -match '\.wms:' } | Select-Object -First 1))
Write-Host "wmsbuild: OK $wms"
