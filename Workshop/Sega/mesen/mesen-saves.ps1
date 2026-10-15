<#
.SYNOPSIS
  A battery game saved in play and restarted from its save, compared with Mesen 2.1.1 (the Master System / Game Gear oracle, GPL: run it, never read it).

.DESCRIPTION
  For every row of roster-saves.tsv (the games with a battery whose own save command a script of button taps reaches):
    pass 1  both emulators start with no save and play the pad script up to and past the game's own save; at each checkpoint the pictures and the cartridge RAM
            (this emulator's save file against the oracle's cartridge RAM, 32 KB) are compared (mesen-compare.ps1 -Dump: columns <mode>_checkpoints and <mode>_save);
    pass 2  this emulator's save file (--battery-out of pass 1) is given to a NEW run of both (the oracle finds it in its Saves folder: mesen-capture.ps1 -SaveIn) and the
            pad script walks the game's own CONTINUE / LOAD to the saved place; the same comparison.
  Exit 0 when every checkpoint of both passes is identical in both timing modes (the app's own Z80 timing, and the oracle's instruction lengths) and every save compare is identical.
  The oracle runs headless (no window, no oracle-lock needed). Two oracle runs per game, so a frame where its two runs disagree would be counted apart (none was in A4).

.EXAMPLE
  pwsh -File mesen-saves.ps1 -RomDir D:\roms\roster -Scratch C:\bn_saves
#>
param(
    [Parameter(Mandatory)] [string]$RomDir,
    [string]$Scratch = "C:\bn_saves",                     # keep it shallow: the oracle's script fails silently when a path passes 260 characters
    [string]$Workshop = (Join-Path $env:LOCALAPPDATA "VRUN_Nes_Dev_work\bn_sega_a\bin\BrokenNes.Workshop\release\BrokenNes.Workshop.exe"),
    [string]$Only = "",                                    # part of a game's file name
    [string]$Modes = "zilog=;oracle=--tune OracleZ80Timing=1"
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $Workshop)) { throw "Workshop exe not found: $Workshop" }
$compare = Join-Path $PSScriptRoot "mesen-compare.ps1"
New-Item -ItemType Directory -Force $Scratch | Out-Null
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
    $a = @("--sms-run", $rom, "--frames", $f[2], "--input", $f[1], "--battery-out", $sav, "--out", $dir, "--tag", "p1", "--region", "Ntsc"); if ($gg) { $a += @("--console", "gg") }
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
        "$rom`t$($p.Pad)`tNtsc`t$($p.Frames)`t$($p.Battery)" | Set-Content $list -Encoding utf8
        $tsv = Join-Path $dir "pass$($p.Id).tsv"
        & $compare -List $list -OutTsv $tsv -Scratch (Join-Path $dir "w$($p.Id)") -Workshop $Workshop -Frames $p.Frames -Spec $p.Spec -Dump -Runs 2 -Jobs 1 -Modes $Modes | Out-Null
        $r = Import-Csv $tsv -Delimiter "`t" | Select-Object -First 1
        foreach ($m in ($Modes -split ";" | ForEach-Object { ($_ -split "=", 2)[0].Trim() })) {
            $cp = [string]$r."$($m)_checkpoints"; $sv = [string]$r."$($m)_save"
            $okPics = $cp -and -not ($cp -split "," | Where-Object { ($_ -split ":")[1] -ne "0" })
            $okSave = $sv -and -not ($sv -split "," | Where-Object { ($_ -split ":")[1] -ne "identical" })
            Write-Host ("  pass {0} [{1}]: pictures {2}  cartridge RAM {3}   {4}" -f $p.Id, $m, $(if ($okPics) { "identical" } else { "DIFFER" }), $(if ($okSave) { "identical" } else { "DIFFER" }), $r.error)
            if (-not ($okPics -and $okSave) -or $r.error) { $bad++ }
        }
    }
}
if ($bad) { Write-Host "$bad comparison(s) differ" -ForegroundColor Red; exit 1 }
Write-Host "every pass of every game is identical to the oracle" -ForegroundColor Green
exit 0
