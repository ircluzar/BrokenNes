<#
.SYNOPSIS
  Runs the probes of vram-probe.py in Mesen 2.1.1 (the oracle: run, never read) and in this emulator (the same power-on line) and compares what reached VRAM and the picture.

.DESCRIPTION
  Per probe (frame 4 of each):
    -Mode vram  which of the 64 bytes the program wrote ($01..$40) are in VRAM $0800.. in the oracle and here, in order. A byte the oracle lost makes every later one move down by one.
    -Mode row   the picture (oracle against this emulator) and the contents of the name table row the program rewrote: a probe whose contents are equal and whose picture is not would be a difference in
                WHEN a write takes effect; one whose contents differ is a difference in WHETHER it is accepted.
  Prints one line per probe and a summary. The oracle runs headless (no window, no oracle-lock needed). Needs Python with Pillow for -Mode row.

.EXAMPLE
  python vram-probe.py C:\bn\p1 --kind otir --mode vram
  pwsh -File vram-probe-sweep.ps1 -Dir C:\bn\p1 -Mode vram
#>
param(
    [Parameter(Mandatory)] [string]$Dir,
    [ValidateSet("vram", "row")] [string]$Mode = "vram",
    [string]$Scratch = "C:\bn_probe",
    [string]$Workshop = (Join-Path $env:LOCALAPPDATA "VRUN_Nes_Dev_work\bn_sega_a\bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe"),
    [string]$Tune = "",
    [string]$Python = $(if (Get-Command python3 -ErrorAction SilentlyContinue) { "python3" } else { "python" })      # with Pillow for -Mode row
)
$ErrorActionPreference = "Stop"
$cap = Join-Path $PSScriptRoot "mesen-capture.ps1"
New-Item -ItemType Directory -Force $Scratch | Out-Null
$py = @'
import sys
mode, ours_png, orc_png, ours_vram, orc_vram, name, line = sys.argv[1:8]
va = open(ours_vram, 'rb').read(); vb = open(orc_vram, 'rb').read()
if mode == 'vram':
    def seq(v): return [v[0x800 + i] for i in range(64)]
    def lost(v):
        s = seq(v); return [i + 1 for i in range(64) if (i + 1) not in s]
    lo, lm = lost(vb), lost(va)
    print('%s (power-on line %s): oracle lost %d byte(s) %s; this emulator lost %d; %s' % (name, line, len(lo), ['%02X' % x for x in lo], len(lm), 'EQUAL' if seq(va) == seq(vb) else 'DIFFERENT'))
else:
    from PIL import Image
    import numpy as np
    A = np.array(Image.open(ours_png).convert('RGB')); B = np.array(Image.open(orc_png).convert('RGB'))
    if B.shape[0] == 240: B = B[24:216]
    h = min(A.shape[0], B.shape[0])
    d = int((A[:h] != B[:h]).any(axis=2).sum())
    row = slice(0x3800 + 6 * 64, 0x3800 + 7 * 64)
    print('%s (power-on line %s): picture differs in %d px; the name table row %s' % (name, line, d, 'EQUAL' if va[row] == vb[row] else 'DIFFERENT'))
'@
$pyFile = Join-Path $Scratch "cmp.py"; Set-Content $pyFile $py -Encoding utf8
$n = 0; $equal = 0; $samePic = 0; $eqNotPic = 0
foreach ($rom in Get-ChildItem $Dir -Filter "probe_$Mode*.sms" | Sort-Object Name) {
    $o = Join-Path $Scratch "oracle"; if (Test-Path $o) { Remove-Item -LiteralPath $o -Recurse -Force }
    & pwsh -NoProfile -File $cap -Rom $rom.FullName -Frames "4" -OutDir $o -Tag p -Pad "" -Region Ntsc -Overscan 0 -Work (Join-Path $Scratch "mesen") -TimeoutSec 100 -Dump | Out-Null
    $line = [regex]::Match((Get-Content (Join-Path $o "p.phase.txt")), 'scanline=(\d+)').Groups[1].Value
    $m = Join-Path $Scratch "mine"
    $a = @("--sms-run", $rom.FullName, "--frames", "5", "--png-at", "4", "--ram-at", "4", "--out", $m, "--tag", "p", "--start-line", $line)
    if ($Tune) { $a += @("--tune", $Tune) }
    & $Workshop @a | Out-Null
    $r = & $Python $pyFile $Mode (Join-Path $m "p_f4.png") (Join-Path $o "p_f4.png") (Join-Path $m "p_f4.vram.bin") (Join-Path $o "p_f4.vram.bin") $rom.BaseName $line
    $r
    $n++
    if ($r -match "EQUAL") { $equal++ }
    if ($Mode -eq "row") {
        if ($r -match "picture differs in 0 px") { $samePic++ }
        if ($r -match "row EQUAL" -and $r -notmatch "differs in 0 px") { $eqNotPic++ }
    }
}
if ($Mode -eq "vram") { Write-Host "$n probes: VRAM bytes equal to the oracle's in $equal" }
else { Write-Host "$n probes: name table row equal in $equal, picture identical in $samePic; row equal but picture different in $eqNotPic (a difference in WHEN a write takes effect)" }
