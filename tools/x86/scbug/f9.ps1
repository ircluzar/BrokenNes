$PD="C:\BrokenNes-work\sc98\pair"; $Exe="C:/BrokenNes-work/scbug_desk/BrokenNes.Windows.exe"
$env:BROKENNES_CONFIG_DIR="C:\BrokenNes-work\laneC\cfg"; $env:BROKENNES_SAVES_DIR="C:\BrokenNes-work\scbug\f9saves"; $env:BROKENNES_NO_DIALOGS="1"; $env:BROKENNES_BUGTIMING="1"
$env:BROKENNES_X86_HDD2="$PD\hd_scpairD.img"; $env:BROKENNES_X86_CD="C:\BrokenNes-work\x86_cds\starcraft_xp_cd1.iso"; $env:BROKENNES_X86_MACHINESTATE="$PD\st_sbingame2.bin"
$proc=Start-Process $Exe -PassThru
$f="$env:LOCALAPPDATA\BrokenNes\instances\$($proc.Id).json"
for($i=0;$i -lt 60 -and -not (Test-Path $f);$i++){Start-Sleep 1}
$u="http://127.0.0.1:"+(Get-Content $f|ConvertFrom-Json).httpPort
for($i=0;$i -lt 60;$i++){ try{ if((irm "$u/api/health").uiReady){break} }catch{}; Start-Sleep 1 }
irm "$u/api/emulator/load-rom" -Method Post -ContentType application/json -Body (@{path="$PD\hd_scpairC_256.img"}|ConvertTo-Json) | Out-Null
Start-Sleep 45
foreach($d in 0,30,60,90,120){ Start-Sleep -Milliseconds $d; $t=[Diagnostics.Stopwatch]::StartNew(); try { $r=irm "$u/api/bugs/capture" -Method Post; "capture ok $([int]$t.ElapsedMilliseconds) ms: $($r.path) frames=$($r.frames) start=$($r.start) noMovie=$($r.noMovie)" } catch { "capture FAIL: $_" } }
"status: " + ((irm "$u/api/bugs/status")|ConvertTo-Json -Compress)
$proc.CloseMainWindow()|Out-Null; Start-Sleep 5; if(-not $proc.HasExited){ Stop-Process -Id $proc.Id }
