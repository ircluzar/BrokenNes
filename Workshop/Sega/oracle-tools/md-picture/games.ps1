param([string[]]$Only = @())
# Scripts per roster game: wall-clock key presses, then pause+snap series. Names follow sega-rosters.md numbering.
$g = if ($env:SEGA_ROMS) { $env:SEGA_ROMS.TrimEnd("\") + "\" } else { "X:\EMULATION\Sega Genesis\" }   # the roster ROMs (never in git)
$snaps = "pause,snap,resume,w2500,pause,snap,resume,w3000,pause,snap,resume,w3500,pause,snap,resume,w4000,pause,snap"
$games = [ordered]@{
  "md01" = @("Flicky (JUE) [!].bin", "w2000,k:Start,w4000,d:Right,w1500,$snaps");
  "md02" = @("Altered Beast (JU) (REV 01) [!].bin", "w3000,k:Start,w5000,k:Start,w6000,k:Start,w8000,d:Right,w1500,$snaps");
  "md03" = @("Sonic the Hedgehog (JUE) [!].bin", "w4000,k:Start,w7000,d:Right,w1500,$snaps");
  "md04" = @("Golden Axe (JU) (REV 00) [!].bin", "w5000,k:Start,w3000,k:Start,w3000,k:Start,w9000,d:Right,w1500,$snaps");
  "md05" = @("Strider (UE) [!].bin", "w6000,k:Start,w4000,k:Start,w6000,d:Right,w1500,$snaps");
  "md06" = @("Streets of Rage (JUE) (REV 00) [!].bin", "w6000,k:Start,w4000,k:Start,w3000,k:Start,w8000,d:Right,w1500,$snaps");
  "md07" = @("Mickey Mouse - Castle of Illusion (U) [!].bin", "w5000,k:Start,w3000,k:Start,w8000,d:Right,w1500,$snaps");
  "md08" = @("ECCO (U) [!].bin", "w7000,k:Start,w6000,k:Start,w8000,d:Right,w1500,$snaps");
  "md09" = @("Sonic the Hedgehog 2 (JUE) [!].bin", "w6000,k:Start,w3000,k:Start,w3000,k:Start,w9000,d:Right,w1500,$snaps");
  "md10" = @("Kid Chameleon (JUE) [!].bin", "w6000,k:Start,w3000,k:Start,w3000,k:Start,w9000,d:Right,w1500,$snaps");
}
foreach ($k in $games.Keys) {
  if ($Only.Count -gt 0 -and $Only -notcontains $k) { continue }
  "=== $k $($games[$k][0])"
  & (Join-Path $PSScriptRoot "cmp.ps1") -Rom ($g + $games[$k][0]) -Name $k -StartupMs 3000 -Script $games[$k][1] 2>&1 | Where-Object { $_ -notmatch "shots requested" } | ForEach-Object { $_.ToString().Substring(0, [Math]::Min(150, $_.ToString().Length)) }
}
