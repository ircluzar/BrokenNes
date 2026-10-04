[CmdletBinding(PositionalBinding=$false)]
param([string]$OnlyList = "", [switch]$NoCapture)
# For each roster game: BlastEm runs a wall-clock script (key presses and screenshots); our emulator runs the same presses at the frames they would fall on (60 fps, with the window opening 1 s before the script starts) and
# --md-find --scan looks, for every screenshot, for the frame of ours that matches it best (0 differing pixels = an exact match somewhere in the run).
$root = Split-Path $MyInvocation.MyCommand.Path
$g = if ($env:SEGA_ROMS) { $env:SEGA_ROMS.TrimEnd("\") + "\" } else { "X:\EMULATION\Sega Genesis\" }   # the roster ROMs (never in git)
# each script: list of (seconds-from-script-start, action) where action = shot | Start | A | Right-down | Right-up ...
$games = [ordered]@{
  "md01 Flicky"                 = @("Flicky (JUE) [!].bin", @(@(2,'shot'),@(4,'Start'),@(7,'shot'),@(10,'shot'),@(12,'d:Right'),@(14,'shot'),@(17,'shot'),@(20,'shot')));
  "md02 Altered Beast"          = @("Altered Beast (JU) (REV 01) [!].bin", @(@(2,'shot'),@(5,'shot'),@(7,'Start'),@(9,'shot'),@(12,'Start'),@(14,'shot'),@(17,'Start'),@(20,'shot'),@(23,'shot'),@(26,'shot')));
  "md03 Sonic 1"                = @("Sonic the Hedgehog (JUE) [!].bin", @(@(3,'shot'),@(7,'shot'),@(10,'shot'),@(11,'Start'),@(14,'shot'),@(18,'shot'),@(20,'d:Right'),@(23,'shot'),@(26,'shot'),@(30,'shot')));
  "md04 Golden Axe"             = @("Golden Axe (JU) (REV 00) [!].bin", @(@(3,'shot'),@(6,'shot'),@(8,'Start'),@(11,'shot'),@(13,'Start'),@(16,'shot'),@(18,'Start'),@(21,'shot'),@(25,'shot'),@(29,'shot')));
  "md05 Strider"                = @("Strider (UE) [!].bin", @(@(3,'shot'),@(7,'shot'),@(9,'Start'),@(12,'shot'),@(15,'shot'),@(17,'Start'),@(20,'shot'),@(24,'shot'),@(28,'shot')));
  "md06 Streets of Rage"        = @("Streets of Rage (JUE) (REV 00) [!].bin", @(@(3,'shot'),@(6,'shot'),@(9,'shot'),@(11,'Start'),@(14,'shot'),@(16,'Start'),@(19,'shot'),@(22,'Start'),@(26,'shot'),@(30,'shot')));
  "md07 Castle of Illusion"     = @("Mickey Mouse - Castle of Illusion (U) [!].bin", @(@(3,'shot'),@(6,'shot'),@(9,'shot'),@(11,'Start'),@(14,'shot'),@(17,'shot'),@(19,'Start'),@(23,'shot'),@(27,'shot')));
  "md08 Ecco"                   = @("ECCO (U) [!].bin", @(@(4,'shot'),@(8,'shot'),@(11,'Start'),@(14,'shot'),@(17,'shot'),@(20,'Start'),@(24,'shot'),@(28,'shot')));
  "md09 Sonic 2"                = @("Sonic the Hedgehog 2 (JUE) [!].bin", @(@(3,'shot'),@(6,'shot'),@(9,'Start'),@(12,'shot'),@(15,'shot'),@(17,'Start'),@(20,'shot'),@(23,'Start'),@(27,'shot'),@(31,'shot')));
  "md10 Kid Chameleon"          = @("Kid Chameleon (JUE) [!].bin", @(@(3,'shot'),@(6,'shot'),@(9,'Start'),@(12,'shot'),@(15,'shot'),@(18,'Start'),@(21,'shot'),@(25,'shot'),@(29,'shot')));
}
$Only = @($OnlyList.Split(",", [StringSplitOptions]::RemoveEmptyEntries))
foreach ($name in $games.Keys) {
  if ($Only.Count -gt 0 -and -not ($Only | Where-Object { $name -like "*$_*" })) { continue }
  $rom = $g + $games[$name][0]; $steps = $games[$name][1]
  $key = ($name -split ' ')[0]
  $dir = Join-Path $root "be\ros_$key"
  # BlastEm script: waits between steps
  $script = @(); $t = 0.0
  foreach ($s in $steps) { $dt = [int](($s[0] - $t) * 1000); if ($dt -gt 0) { $script += "w$dt" }; $t = $s[0]; $a = $s[1]; $script += $(if ($a -eq 'shot') { 'shot' } elseif ($a.StartsWith('d:')) { $a } else { "k:$a" }) }
  if (-not $NoCapture) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue; & (Join-Path $root "bl.ps1") -Rom $rom -OutDir $dir -Script ($script -join ',') -StartupMs 1000 | Out-Null }
  $shots = (Get-ChildItem $dir -Filter *.png | Sort-Object Name | ForEach-Object { Join-Path $dir $_.Name }) -join ','
  # our inputs: the same presses at frame = (startup 1 s + seconds) * 59.92
  $inp = @(); foreach ($s in $steps) { $f = [int](($s[0] + 1.3) * 59.92); $a = $s[1]; if ($a -eq 'shot') { continue }; if ($a.StartsWith('d:')) { $inp += "${f}:$($a.Substring(2))" } else { $inp += "${f}:$a"; $inp += "$($f+6):" } }
  $r = & (Join-Path $root "ws.ps1") --md-find $rom --shots $shots --scan --from 0 --to 2400 --dys "9" --input ($inp -join ',')
  "=== $name  (shots: $((Get-ChildItem $dir -Filter *.png).Count))  inputs: $($inp -join ' ')"
  $r | Select-String "shot " | ForEach-Object { $_.Line }
}

