<#
.SYNOPSIS
  The import half of the BizHawk .bk2 check (P0 of the compatibility catalog): EmuHawk records a .bk2 by itself, BrokenNes imports it
  (Workshop --movie import --bk2) and must press exactly what BizHawk recorded, at the frames it recorded it.

.DESCRIPTION
  Per case (a built project and a BizHawk emulator of BrokenDev's registry): a BrokenNes movie with no input is exported as a .bk2 for the
  emulator's core (an empty movie of the right length, the only way to start EmuHawk 2.11.1 recording from Lua); EmuHawk plays it with
  tools\compat\bizhawk-record.lua, which turns it to record mode at power-on, presses a pad script by BizHawk's own button names and saves the
  movie BizHawk wrote. That .bk2 is imported into BrokenNes; the imported movie must equal a BrokenNes recording of the same script by
  BrokenNes's names: the same input changes and the same keyframes (picture and RAM hashes, every 60 frames).
  Negative controls: the BizHawk movie imported for another console's ROM is refused; a BrokenNes recording without the first press differs.
  Output: BROKENDEV_HOME\work\bk2-import. Exit 0 when every check that ran passed.
#>
[CmdletBinding()]
param([string]$WorkshopExe = '', [string[]]$Case = @('nes', 'snes', 'gb', 'sms', 'gg', 'md'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$bd = Split-Path -Parent $here
foreach ($l in 'Common', 'Tools', 'Archive', 'Toolchains', 'Manifest', 'Build', 'Verify', 'Emulators', 'Compat', 'Movies') { . (Join-Path $bd "lib\$l.ps1") }
. (Join-Path $bd 'lib\adapters\bizhawk.ps1')
if ($WorkshopExe) { $env:BROKENDEV_WORKSHOP = (Resolve-Path -LiteralPath $WorkshopExe).ProviderPath }

$script:checks = 0; $script:failed = @(); $script:skipped = 0
function Check { param([string]$Name, [bool]$Ok, [string]$Detail) $script:checks++; if ($Ok) { Write-Host "  PASS  ${Name}: $Detail" -ForegroundColor Green } else { $script:failed += $Name; Write-Host "  FAIL  ${Name}: $Detail" -ForegroundColor Red } }
function Skip { param([string]$Name, [string]$Why) $script:skipped++; Write-Host "  SKIP  ${Name}: $Why" -ForegroundColor Yellow }

# case -> project, emulator, frames, the script by BizHawk's names (frame:names), the same by BrokenNes's names
$cases = @{
    nes  = @{ Project = 'vrun-nes';     Emu = 'bizhawk-neshawk';    Frames = 600; Biz = '120:P1 Start,132:,400:P1 Right,450:P1 Right+P1 B,500:'; Bn = '120:Start,132:,400:Right,450:Right+B,500:' }
    snes = @{ Project = 'supervrun';    Emu = 'bizhawk-bsnes';      Frames = 600; Biz = '120:P1 Start,132:,400:P1 Right,450:P1 Right+P1 Y,500:'; Bn = '120:Start,132:,400:Right,450:Right+Y,500:' }
    gb   = @{ Project = 'vrun-voc';     Emu = 'bizhawk-sameboy';    Frames = 600; Biz = '300:Start,310:,450:A,460:,500:Right,550:';             Bn = '300:Start,310:,450:A,460:,500:Right,550:' }
    sms  = @{ Project = 'vrun-ctw-sms'; Emu = 'bizhawk-gpgx-sms';   Frames = 600; Biz = '120:P1 B2,132:,300:Pause,302:,400:P1 Right,500:';      Bn = '120:B,132:,300:Select,302:,400:Right,500:' }
    gg   = @{ Project = 'vrun-voc-gg';  Emu = 'bizhawk-smshawk-gg'; Frames = 700; Biz = '60:P1 B1,70:,400:P1 Right,410:,600:P1 Start,610:';      Bn = '60:A,70:,400:Right,410:,600:Start,610:' }
    md   = @{ Project = 'supervrun-md'; Emu = 'bizhawk-gpgx';       Frames = 700; Biz = '300:P1 Start,310:,420:P1 Start,430:,600:P1 C,610:';      Bn = '300:Start,310:,420:Start,430:,600:C,610:' }
}
$work = Join-Path (Join-Path (Get-BdHome) 'work') 'bk2-import'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$J = Read-BdEmulators
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Lines { param([string]$Path, [string]$Kind) @([System.IO.File]::ReadAllLines($Path) | Where-Object { $_ -match "^$Kind " }) }

foreach ($cn in @($Case | ForEach-Object { $_ -split ',' } | Where-Object { $_ })) {   # pwsh -File passes 'a,b' as one string
    $c = $cases[$cn]
    Write-Host "== $cn ($($c.Project) on $($c.Emu))"
    try { $p = Get-BdProjectRomAndSymbols -Id $c.Project } catch { Skip $cn "$_"; continue }
    $e = Get-BdEmulator -Id $c.Emu -J $J
    $exe = Get-BdEmuExe -Emu $e -J $J
    if (-not $exe) { Skip $cn "$($c.Emu) is not installed"; continue }
    $dir = Join-Path $work $cn
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    $scr = Join-Path $dir 'scratch'
    New-Item -ItemType Directory -Force -Path $dir, $scr, (Join-Path $scr 'saves') | Out-Null
    $romCopy = Join-Path $scr (Split-Path -Leaf $p.Rom); Copy-Item -LiteralPath $p.Rom -Destination $romCopy -Force
    # 1. the empty movie, as a .bk2 for the emulator's core
    $blank = Join-Path $dir 'blank.bnm'; $blankBk2 = Join-Path $dir 'blank.bk2'
    $r = Invoke-BdWorkshopMovie -Arguments @('record', '--rom', $p.Rom, '--frames', '1', '--out', $blank)
    if ($r.ExitCode -ne 0) { Check "$cn record" $false $r.Output; continue }
    $r = Invoke-BdWorkshopMovie -Arguments @('export', '--movie', $blank, '--rom', $p.Rom, '--bk2', $blankBk2, '--core', [string]$e.core)
    if ($r.ExitCode -ne 0) { Check "$cn export" $false $r.Output; continue }
    # 2. EmuHawk records the script into it
    $rec = Join-Path $dir 'bizhawk.bk2'; $recBase = Join-Path $dir 'bizhawk'; $done = Join-Path $dir 'done.txt'
    $spec = @("frames $($c.Frames)", "out $recBase", "done $done")
    $prev = $null
    foreach ($s in ($c.Biz -split ',')) {
        $k = $s.IndexOf(':'); $names = $s.Substring($k + 1); if (-not $names) { $names = '-' }
        $spec += "i $($s.Substring(0, $k)) $($names -replace ' ', '_SP_')"
    }
    # names hold spaces ("P1 B1"): the spec writes them with _SP_, the Lua script reads them back
    $specPath = Join-Path $dir 'record-spec.txt'
    [System.IO.File]::WriteAllText($specPath, (($spec -join "`n") + "`n"), $utf8)
    $ec = Test-BdBizhawkEmu -Emu $e
    $cfgPath = Join-Path $scr 'config.ini'
    $cfg = New-BdBizhawkConfig -System $ec.System -Core $ec.Core -SaveDir (Join-Path $scr 'saves') -Scratch $scr -InstallDir (Split-Path -Parent $exe) -Mode compat
    # the movie's end: switch to recording (EmuHawk's MovieEndAction; its default stops at FINISHED)
    $cfg['Movies'] = [ordered]@{ MovieEndAction = $(if ($env:BDR_ENDACTION) { [int]$env:BDR_ENDACTION } else { 2 }) }
    Save-BdJson -Object $cfg -Path $cfgPath -Depth 8
    $lua = Join-Path $bd 'tools\compat\bizhawk-record.lua'
    $run = Invoke-BdWatchedProcess -FilePath $exe -Arguments @("--config=$cfgPath", '--chromeless', "--lua=$lua", "--movie=$blankBk2", $romCopy) -WorkingDirectory $scr `
        -Env @{ BDR_SPEC = $specPath } -ResultFile $done -TimeoutSec 600 -HangSec 600 -LogFile (Join-Path $scr 'bizhawk.log')
    $doneText = if (Test-Path -LiteralPath $done) { ([System.IO.File]::ReadAllText($done)).Trim() } else { "no result (exit $($run.ExitCode))" }
    $okRec = (Test-Path -LiteralPath $rec) -and $doneText -like 'done*'
    Check "$cn EmuHawk recorded a .bk2 by itself" $okRec $doneText
    if (-not $okRec) { continue }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [System.IO.Compression.ZipFile]::OpenRead($rec)
    try { $hdr = (New-Object System.IO.StreamReader($z.GetEntry('Header.txt').Open())).ReadToEnd() } finally { $z.Dispose() }
    Write-Host "  INFO  BizHawk's header: $(($hdr -split "`n" | Where-Object { $_ -match '^(Platform|Core|emuVersion|SHA1) ' }) -join ' | ')"
    # 3. import, and a BrokenNes recording of the same script
    $imp = Join-Path $dir 'imported.bnm'; $ref = Join-Path $dir 'brokennes.bnm'; $late = Join-Path $dir 'late.bnm'
    $ri = Invoke-BdWorkshopMovie -Arguments @('import', '--bk2', $rec, '--rom', $p.Rom, '--out', $imp)
    if ($ri.ExitCode -ne 0) { Check "$cn import" $false $ri.Output; continue }
    $rr = Invoke-BdWorkshopMovie -Arguments @('record', '--rom', $p.Rom, '--frames', [string]$c.Frames, '--input', $c.Bn, '--out', $ref)
    $first = ($c.Bn -split ',')[0]; $fk = $first.IndexOf(':')
    $lateScript = $c.Bn.Replace($first, "$($first.Substring(0, $fk)):")   # the first press left out
    $rl = Invoke-BdWorkshopMovie -Arguments @('record', '--rom', $p.Rom, '--frames', [string]$c.Frames, '--input', $lateScript, '--out', $late)
    if ($rr.ExitCode -ne 0 -or $rl.ExitCode -ne 0) { Check "$cn reference record" $false "$($rr.Output) $($rl.Output)"; continue }
    $ii = Lines $imp 'i'; $ir = Lines $ref 'i'; $ki = Lines $imp 'k'; $kr = Lines $ref 'k'; $kl = Lines $late 'k'
    Check "$cn imported movie presses what BizHawk recorded, at its frames" (($ii -join ';') -eq ($ir -join ';')) ("imported: " + ($ii -join ', ') + " | BrokenNes: " + ($ir -join ', '))
    Check "$cn imported movie's keyframes (picture + RAM) equal BrokenNes's own recording" ($ki.Count -gt 0 -and ($ki -join ';') -eq ($kr -join ';')) "$($ki.Count) keyframes"
    Check "NEGATIVE: $cn a recording without the first press has other keyframes" (($kl -join ';') -ne ($kr -join ';')) 'differs'
    # 4. the BizHawk movie on another console's ROM is refused
    $otherRom = if ($cn -eq 'nes') { (Get-BdProjectRomAndSymbols -Id 'supervrun').Rom } else { (Get-BdProjectRomAndSymbols -Id 'vrun-nes').Rom }
    $rx = Invoke-BdWorkshopMovie -Arguments @('import', '--bk2', $rec, '--rom', $otherRom, '--out', (Join-Path $dir 'wrong.bnm'))
    Check "NEGATIVE: $cn the .bk2 imported for another console's ROM is refused" ($rx.ExitCode -ne 0 -and $rx.Output -match 'BizHawk') (($rx.Output -split "`n" | Where-Object { $_ -match 'bk2' } | Select-Object -First 1))
}
Write-Host ''
$ok = $script:failed.Count -eq 0
Write-Host ('{{"pass": {0}, "checks": {1}, "failed": {2}, "skipped": {3}}}' -f $ok.ToString().ToLowerInvariant(), $script:checks, $script:failed.Count, $script:skipped)
if ($ok) { exit 0 } else { exit 1 }
