# wprprof.ps1 - RUN FROM AN ELEVATED PowerShell (WPR kernel sampling needs admin).
# Records st_sbingame2 in-game (800 frames) with ETW CPU sampling + stacks + .NET JIT rundown.
# Output: C:\BrokenNes-work\scspeed\wpr_ingame.etl (the agent analyses it with wpaexporter, no admin needed).
$ErrorActionPreference = 'Continue'
$wpr = "C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\wpr.exe"
$P   = "C:/BrokenNes-work/sc98/pair"
$exe = "C:\BrokenNes-work\x86speed_bin\BrokenNes.Workshop.exe"
$out = "C:\BrokenNes-work\scspeed\wpr_ingame.etl"
$sh  = "C:\Program Files\Git\bin\sh.exe"
# heavy slot (token-checked)
$c = & $sh C:/BrokenNes-work/heavylock.sh claim "wprprof (user elevated)"
while ($LASTEXITCODE -ne 0) { Start-Sleep 20; $c = & $sh C:/BrokenNes-work/heavylock.sh claim "wprprof (user elevated)" }
$slot, $tok = ($c -split '\s+')
try {
  # -cancel complains on stderr when nothing is recording; that is fine, so don't let it stop the script
  $ErrorActionPreference = 'Continue'
  & $wpr -cancel 2>&1 | Out-Null
  & $wpr -start CPU -start DotNET -filemode
  if ($LASTEXITCODE -ne 0) { throw "wpr -start failed (exit $LASTEXITCODE)" }
  $env:X86_NONIC = '1'
  & $exe --x86boot --machine WIN98-P166 --hdd "$P/hd_scpairC_256.img" --hdd2 "$P/hd_scpairD.img" `
     --cd C:/BrokenNes-work/x86_cds/starcraft_xp_cd1.iso --apu SB16_D072 `
     --loadstate "$P/st_sbingame2.bin" --frames 800 *> C:\BrokenNes-work\scspeed\wpr_ingame.txt; Write-Host "emulator exit $LASTEXITCODE"
  & $wpr -stop $out
  if ($LASTEXITCODE -ne 0) { throw "wpr -stop failed (exit $LASTEXITCODE)" }
} finally {
  & $sh C:/BrokenNes-work/heavylock.sh release $slot $tok
}
Write-Host "done -> $out"

