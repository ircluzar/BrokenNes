# build.ps1: builds N98LAUNCH.EXE (Win32 GUI, runs on Windows 95/98) with the Open Watcom in C:\BrokenNes-tools\dos
. C:\BrokenNes-tools\dos\env.ps1
Set-Location $PSScriptRoot
& wcl386 -q -bt=nt -l=nt_win -ox '-fe=N98LAUNCH.EXE' n98launch.c
Remove-Item -ErrorAction SilentlyContinue n98launch.obj
& wcl386 -q -bt=nt -l=nt_win -ox '-fe=N98INST.EXE' n98inst.c
Remove-Item -ErrorAction SilentlyContinue n98inst.obj
