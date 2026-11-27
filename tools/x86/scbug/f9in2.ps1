$PD="C:\BrokenNes-work\sc98\pair"; $Exe="C:/BrokenNes-work/scbug_desk/BrokenNes.Windows.exe"
$env:BROKENNES_CONFIG_DIR = $(if ($env:SCB_CFG) { $env:SCB_CFG } else { "C:\BrokenNes-work\laneC\cfg" }); $env:BROKENNES_SAVES_DIR="C:\BrokenNes-work\scbug\f9saves"; $env:BROKENNES_NO_DIALOGS="1"; $env:BROKENNES_BUGTIMING="1"
$env:BROKENNES_X86_HDD2="$PD\hd_scpairD.img"; $env:BROKENNES_X86_CD="C:\BrokenNes-work\x86_cds\starcraft_xp_cd1.iso"; $env:BROKENNES_X86_MACHINESTATE="$PD\st_sbingame2.bin"
$proc=Start-Process $Exe -PassThru
$f="$env:LOCALAPPDATA\BrokenNes\instances\$($proc.Id).json"
for($i=0;$i -lt 60 -and -not (Test-Path $f);$i++){Start-Sleep 1}
$u="http://127.0.0.1:"+(Get-Content $f|ConvertFrom-Json).httpPort
for($i=0;$i -lt 60;$i++){ try{ if((irm "$u/api/health").uiReady){break} }catch{}; Start-Sleep 1 }
irm "$u/api/emulator/load-rom" -Method Post -ContentType application/json -Body (@{path="$PD\hd_scpairC_256.img"}|ConvertTo-Json) | Out-Null
Start-Sleep 45
Start-Sleep 40
try { irm "$u/api/input/pc-key" -Method Post -ContentType application/json -Body (@{scan=0x1E;extended=$false;down=$true}|ConvertTo-Json) | Out-Null; "gate: pc-key ACCEPTED" } catch { "gate: pc-key refused " + $_.Exception.Response.StatusCode.value__ }
try { irm "$u/api/input/pc-mouse" -Method Post -ContentType application/json -Body (@{dx=1;dy=1;buttons=0}|ConvertTo-Json) | Out-Null; "gate: pc-mouse ACCEPTED" } catch { "gate: pc-mouse refused " + $_.Exception.Response.StatusCode.value__ }
$j=@{ContentType='application/json'; Method='Post'}
for($i=0;$i -lt 40;$i++){ irm "$u/api/input/pc-mouse" @j -Body (@{dx=3;dy=2;buttons=0}|ConvertTo-Json) | Out-Null; Start-Sleep -Milliseconds 50 }
irm "$u/api/input/pc-mouse" @j -Body (@{dx=0;dy=0;buttons=1}|ConvertTo-Json) | Out-Null; Start-Sleep -Milliseconds 100
irm "$u/api/input/pc-mouse" @j -Body (@{dx=0;dy=0;buttons=0}|ConvertTo-Json) | Out-Null
foreach($sc in 0x1E,0x30,0x2E){ irm "$u/api/input/pc-key" @j -Body (@{scan=$sc;extended=$false;down=$true}|ConvertTo-Json) | Out-Null; Start-Sleep -Milliseconds 80; irm "$u/api/input/pc-key" @j -Body (@{scan=$sc;extended=$false;down=$false}|ConvertTo-Json) | Out-Null; Start-Sleep -Milliseconds 80 }
Start-Sleep 2
$r=irm "$u/api/bugs/capture" -Method Post; "capture: $($r.path) frames=$($r.frames)"
$proc.CloseMainWindow()|Out-Null; Start-Sleep 5; if(-not $proc.HasExited){ Stop-Process -Id $proc.Id }
