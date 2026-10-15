[CmdletBinding(PositionalBinding=$false)]
param([string]$Games = "all", [int]$Steps = 1500, [int]$WaitMs = 5000, [string]$Pad = "default", [int]$Offset = 90, [int]$ShotEvery = 100, [int]$Retries = 2, [string]$Tag = "", [string]$Log = "")
# The roster climb: each game of docs/projects/sega-rosters.md played through with the sweep's pad script (or --Pad) in lock-step with BlastEm (oraclerun.ps1: pause and frame advance, a picture every -ShotEvery
# advances), one line per game in the log. Needs: the blastem lock (Workshop\Sega\oracle-lock.ps1 -Acquire blastem -Owner <you>), BN_ORACLE_ROOT (a private folder with blastem\, be_shots\, be_save\) and BN_ARTIFACTS
# (the build folder of the Workshop exe). A capture the run itself calls unreliable (the screenshot count is wrong) is repeated up to -Retries times.
$here = Split-Path $MyInvocation.MyCommand.Path
$work = if ($env:BN_ORACLE_ROOT) { $env:BN_ORACLE_ROOT } else { $here }
if (-not $Log) { $Log = Join-Path $work "rbatch$Tag.log" }
$dir = if ($env:SEGA_ROMS) { $env:SEGA_ROMS.TrimEnd("\") } else { "X:\EMULATION\Sega Genesis" }
$roster = @{      # a plain hashtable: on an [ordered] dictionary an integer index is a position, not a key
  1 = "Flicky (JUE) [!].bin"; 2 = "Altered Beast (JU) (REV 01) [!].bin"; 3 = "Sonic the Hedgehog (JUE) [!].bin"; 4 = "Golden Axe (JU) (REV 00) [!].bin"; 5 = "Strider (UE) [!].bin"
  6 = "Streets of Rage (JUE) (REV 00) [!].bin"; 7 = "Mickey Mouse - Castle of Illusion (U) [!].bin"; 8 = "ECCO (U) [!].bin"; 9 = "Sonic the Hedgehog 2 (JUE) [!].bin"; 10 = "Kid Chameleon (JUE) [!].bin"
  11 = "Gunstar Heroes (E) [!].bin"; 12 = "Streets of Rage 2 (U) [!].bin"; 13 = "Shinobi 3 - Return of the Ninja Master (U) [!].bin"; 14 = "Castlevania - Bloodlines (U) [!].bin"; 15 = "Comix Zone (4) [!].bin"
  16 = "Contra - Hard Corps (U) [!].bin"; 17 = "Phantasy Star IV (4) [!].bin"; 18 = "Sonic The Hedgehog 3 (U) [!].gen"; 19 = "Streets of Rage 3 (E).bin"; 20 = "Sonic and Knuckles & Sonic 3 (JUE) [!].bin"
}
$list = @()
if ($Games -eq "all") { $list = 1..20 } else { foreach ($p in $Games.Split(',')) { if ($p -match '^(\d+)-(\d+)$') { $list += [int]$Matches[1]..[int]$Matches[2] } else { $list += [int]$p } } }
foreach ($n in $list) {
  $rom = Join-Path $dir $roster[$n]
  $name = "g{0:D2}$Tag" -f $n
  $result = ""
  for ($try = 1; $try -le ($Retries + 1); $try++) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = & (Join-Path $here "oraclerun.ps1") -Rom $rom -Name $name -Pad $Pad -Steps $Steps -Offset $Offset -ShotEvery $ShotEvery -WaitMs $WaitMs 2>&1 | Out-String
    $unreliable = $out -match "CAPTURE UNRELIABLE"
    $summary = ($out -split "`r?`n" | Where-Object { $_ -match "^compared |^f0 =|AMBIGUOUS|CAPTURE UNRELIABLE|scan: the screenshot|no window|stopped after" }) -join " | "
    $result = "{0} try {1} ({2:N0} s): {3}" -f $name, $try, $sw.Elapsed.TotalSeconds, $summary
    Add-Content $Log ("=== #{0} {1}`r`n{2}`r`n{3}" -f $n, $roster[$n], $result, (($out -split "`r?`n" | Where-Object { $_ -match "^after " }) -join "`r`n"))
    if (-not $unreliable) { break }
  }
  Write-Host "#$n $($roster[$n]): $result"
}
