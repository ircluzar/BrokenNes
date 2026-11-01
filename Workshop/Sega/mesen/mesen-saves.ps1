<#
.SYNOPSIS
  A battery game saved in play and restarted from its save, compared with Mesen 2.1.1 (the Master System / Game Gear oracle, GPL: run it, never read it).

.DESCRIPTION
  For every row of roster-saves.tsv (the games with a battery whose own save command a script of button taps reaches):
    pass 1  both emulators start with no save and play the pad script up to and past the game's own save; at each checkpoint the pictures and the cartridge RAM
            (this emulator's save file against the oracle's cartridge RAM, 32 KB) are compared (mesen-compare.ps1 -Dump: columns <mode>_a_identical and <mode>_save);
    pass 2  this emulator's save file (--battery-out of pass 1) is given to a NEW run of both (the oracle finds it in its Saves folder: mesen-capture.ps1 -SaveIn) and the
            pad script walks the game's own CONTINUE / LOAD to the saved place; the same comparison.
  The FIRST mode of -Modes gates: its pass-1 run also makes the save file pass 2 starts from, and every checkpoint of both passes must be identical to the oracle's first run (all frames compared,
  not only those where its two runs agree) and every cartridge RAM compare identical, or the exit is 1. The other modes are printed as information. The default gate mode is the oracle's Z80
  instruction lengths plus the oracle's line counter at power-on (OracleZ80Timing, OracleLineCounterAtPowerOn: tool settings, never on in the app: see the notes in SmsVdp.cs and sega-track-a4.md); the
  information mode is the app's own configuration. A game that depends on the Z80 lengths for a long play (Shining Force II's first battle) only follows the oracle in the gate mode.
  The oracle's runs are cut into segments chained through its own save states (mesen-capture.ps1 -Segment: its test runner ends a run after 100 seconds).
  The oracle runs headless (no window, no oracle-lock needed).

.EXAMPLE
  pwsh -File mesen-saves.ps1 -RomDir D:\roms\roster -Scratch C:\bn_saves
#>
param(
    [Parameter(Mandatory)] [string]$RomDir,
    [string]$Scratch = "C:\bn_saves",                     # keep it shallow: the oracle's script fails silently when a path passes 260 characters
    [string]$Workshop = (Join-Path $env:LOCALAPPDATA "VRUN_Nes_Dev_work\bn_sega_a\bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe"),
    [string]$Only = "",                                    # part of a game's file name
    [string]$Modes = "oracle=--tune OracleZ80Timing=1,OracleLineCounterAtPowerOn=1;zilog="
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $Workshop)) { throw "Workshop exe not found: $Workshop" }
$compare = Join-Path $PSScriptRoot "mesen-compare.ps1"
New-Item -ItemType Directory -Force $Scratch | Out-Null
$modeList = @($Modes -split ";" | ForEach-Object { ($_ -split "=", 2)[0].Trim() })
$gateTune = @((($Modes -split ";")[0] -split "=", 2)[1] -split " " | Where-Object { $_ })       # the first mode gates (and makes the save pass 2 starts from); the others are information
$bad = 0
foreach ($line in Get-Content (Join-Path $PSScriptRoot "roster-saves.tsv")) {
    if (-not $line.Trim() -or $line.TrimStart().StartsWith("#")) { continue }
    $f = $line -split "`t"
    if ($Only -and $f[0] -notlike "*$Only*") { continue }
    $name = $f[0]; $gg = $name.EndsWith(".gg")
    $rom = (Get-ChildItem -LiteralPath $RomDir -Recurse -File | Where-Object { $_.Name -eq $name } | Select-Object -First 1).FullName
    if (-not $rom) { throw "ROM not found under ${RomDir}: $name" }
    $tag = ($name -replace '[^A-Za-z0-9]+', '_'); if ($tag.Length -gt 12) { $tag = $tag.Substring(0, 12) }
    $dir = Join-Path $Scratch $tag; Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $dir | Out-Null
    $sav = Join-Path $dir "save.sav"
    # this emulator's save from pass 1 (what the apps write at close): the file the second pass starts from
    $a = @("--sms-run", $rom, "--frames", $f[2], "--input", $f[1], "--battery-out", $sav, "--out", $dir, "--tag", "p1", "--region", "Ntsc"); if ($gg) { $a += @("--console", "gg") }; $a += $gateTune
    & $Workshop @a | Out-Null
    if (-not (Test-Path $sav)) { Write-Host "$name : pass 1 wrote no save file" -ForegroundColor Red; $bad++; continue }
    $nz = @([IO.File]::ReadAllBytes($sav) | Where-Object { $_ -ne 0 }).Count
    Write-Host ("{0}: save file {1} bytes, {2} non-zero" -f $name, (Get-Item $sav).Length, $nz)
    $passes = @(
        @{ Id = 1; Pad = $f[1]; Frames = [int]$f[2]; Spec = $f[3]; Battery = "" },
        @{ Id = 2; Pad = $f[4]; Frames = [int]$f[5]; Spec = $f[6]; Battery = $sav }
    )
    foreach ($p in $passes) {
        $list = Join-Path $dir "list$($p.Id).tsv"
        "$rom`t$($p.Pad)`tNtsc`t$($p.Frames)`t`t$($p.Battery)" | Set-Content $list -Encoding utf8      # (the 5th column is the note, left empty; the 6th the save the run starts from)
        $tsv = Join-Path $dir "pass$($p.Id).tsv"
        & $compare -List $list -OutTsv $tsv -Scratch (Join-Path $dir "w$($p.Id)") -Workshop $Workshop -Frames $p.Frames -Spec $p.Spec -Dump -Runs 2 -Jobs 1 -Segment 25000 -Modes $Modes | Out-Null
        $r = Import-Csv $tsv -Delimiter "`t" | Select-Object -First 1
        foreach ($m in $modeList) {
            $ai = [string]$r."$($m)_a_identical"; $af = [string]$r."$($m)_a_frames"; $sv = [string]$r."$($m)_save"
            $okPics = $af -and ($ai -eq $af)
            $okSave = $sv -and -not ($sv -split "," | Where-Object { ($_ -split ":")[1] -ne "identical" })
            $gate = ($m -eq $modeList[0])
            Write-Host ("  pass {0} [{1}{2}]: pictures {3} of {4} identical to the oracle's first run  cartridge RAM {5}   {6}" -f $p.Id, $m, $(if ($gate) { ", gate" } else { ", information" }), $ai, $af, $(if ($okSave) { "identical" } else { "DIFFER" }), $r.error)
            if ($gate -and (-not ($okPics -and $okSave) -or $r.error)) { $bad++ }
        }
    }
}
if ($bad) { Write-Host "$bad gate comparison(s) differ" -ForegroundColor Red; exit 1 }
Write-Host "every pass of every game is identical to the oracle in the gate mode" -ForegroundColor Green
exit 0
