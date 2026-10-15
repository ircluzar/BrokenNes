[CmdletBinding(PositionalBinding=$false)]
param([string]$Games = "all", [int]$Steps = 1500, [int]$WaitMs = 5000, [string]$Pad = "default", [int]$Offset = 90, [int]$ShotEvery = 100, [int]$Retries = 2, [string]$Tag = "", [string]$Log = "", [switch]$AutoPause, [string]$LockOwner = "b4b", [switch]$Saves, [string]$RomFile = "", [string]$Label = "", [switch]$PauseSnap)
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
if ($RomFile) { $roster[99] = $RomFile; $list = @(99) }       # any other ROM of the library: -RomFile "Rampart (UE) [!].bin" -Label rampart
foreach ($n in $list) {
  $rom = Join-Path $dir $roster[$n]
  $name = if ($RomFile -and $Label) { "x_$Label$Tag" } else { "g{0:D2}$Tag" -f $n }
  $result = ""
  # where to pause: the middle of the first stretch (at least 80 boundaries long, after boundary 250) of a run without input whose pictures are seen nowhere else (a still picture matches many boundaries and makes the start ambiguous);
  # a pause at wall-clock t lands at about boundary 62 x (t + 1.3 s)
  $wait = $WaitMs
  if ($AutoPause) {
    $w = (& (Join-Path $here "ws.ps1") -TimeoutSec 600 --md-oracle $rom --windows 2400 --min 80) -join " "
    $pick = $null
    foreach ($m in [regex]::Matches($w, '(\d+)-(\d+)')) { $a = [int]$m.Groups[1].Value; $b = [int]$m.Groups[2].Value; if ($b -gt 250 -and $b - [Math]::Max($a, 250) -ge 80) { $pick = ([Math]::Max($a, 250) + $b) / 2; break } }
    if ($pick) { $wait = [int](($pick / 62 - 1.3) * 1000) }
    Add-Content $Log ("=== #{0} windows: {1} -> pause at {2} ms" -f $n, ($w -replace '.*: ', ''), $wait)
  }
  $gap = 45
  # the window lock is held per game only (about five minutes), so that another agent's window work can take its turn between two games
  $lock = Join-Path $here "..\..\oracle-lock.ps1"
  & pwsh -NoProfile -File $lock -Acquire blastem -Owner $LockOwner -WaitMinutes 90 | Out-Null
  try {
  for ($try = 1; $try -le ($Retries + 1); $try++) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $env:BN_ADV_MS = "$gap"
    $saveDir = Join-Path $work ("be_save\" + [IO.Path]::GetFileNameWithoutExtension($rom))
    if ($Saves) { Remove-Item -LiteralPath $saveDir -Recurse -Force -ErrorAction SilentlyContinue }      # the oracle starts from an empty battery, as ours does
    $out = & (Join-Path $here "oraclerun.ps1") -Rom $rom -Name $name -Pad $Pad -Steps $Steps -Offset $Offset -ShotEvery $ShotEvery -WaitMs $wait -SaveOut:$Saves -PauseSnap:$PauseSnap 2>&1 | Out-String
    if ($Saves) {
      # BlastEm writes its battery file when it exits: compare it with ours byte for byte
      $theirs = Get-ChildItem -LiteralPath $saveDir -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Name -in "save.sram","save.eeprom" } | Select-Object -First 1
      $mine = Join-Path $work "our\$name\save.bin"
      if ($theirs -and (Test-Path $mine)) {
        $tb = [IO.File]::ReadAllBytes($theirs.FullName); $mb = [IO.File]::ReadAllBytes($mine)
        $nz = { param($x) ($x | Where-Object { $_ -ne 0 -and $_ -ne 0xFF }).Count }
        if ($tb.Length -eq $mb.Length) { $diff = 0; $first = -1; for ($i = 0; $i -lt $tb.Length; $i++) { if ($tb[$i] -ne $mb[$i]) { $diff++; if ($first -lt 0) { $first = $i } } }
          $out += "`r`nSAVE: $($theirs.Name) $($tb.Length) bytes ($(& $nz $tb) neither 00 nor FF) against ours $($mb.Length) bytes ($(& $nz $mb)): $(if ($diff -eq 0) { 'IDENTICAL' } else { "$diff bytes differ, first at offset $first (theirs $($tb[$first].ToString('X2')), ours $($mb[$first].ToString('X2')))" })" }
        else { $out += "`r`nSAVE: sizes differ: $($theirs.Name) $($tb.Length) bytes ($(& $nz $tb) neither 00 nor FF), ours $($mb.Length) bytes ($(& $nz $mb))" }
      } else { $out += "`r`nSAVE: no file to compare (oracle: $($theirs -ne $null), ours: $(Test-Path $mine))" }
    }
    $lost = $out -match "advance\(s\) (behind|ahead)"
    $unreliable = ($out -match "CAPTURE UNRELIABLE") -or $lost
    if ($lost) { $out = "TAP LOSS (gap $gap ms): retrying slower`r`n" + $out; $gap = [int]($gap * 2.2) }
    $summary = ($out -split "`r?`n" | Where-Object { $_ -match "^compared |^f0 =|AMBIGUOUS|CAPTURE UNRELIABLE|scan: the screenshot|no window|stopped after|^SAVE:|^wrote the battery" }) -join " | "
    $result = "{0} try {1} ({2:N0} s): {3}" -f $name, $try, $sw.Elapsed.TotalSeconds, $summary
    Add-Content $Log ("=== #{0} {1}`r`n{2}`r`n{3}" -f $n, $roster[$n], $result, (($out -split "`r?`n" | Where-Object { $_ -match "^after " }) -join "`r`n"))
    if (-not $unreliable) { break }
  }
  } finally { & pwsh -NoProfile -File $lock -Release blastem -Owner $LockOwner | Out-Null; Start-Sleep -Seconds 75 }      # a pause after the release: another agent's wait for a free lock counts a minute
  Write-Host "#$n $($roster[$n]): $result"
}
