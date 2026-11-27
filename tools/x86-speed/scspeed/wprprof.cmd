@echo off
rem Right-click this file -> "Run as administrator".
rem Records StarCraft in-game (800 frames) with Windows Performance Recorder.
rem Output: C:\BrokenNes-work\scspeed\wpr_ingame.etl
net session >nul 2>&1
if errorlevel 1 (
  echo This needs administrator rights. Right-click wprprof.cmd and choose "Run as administrator".
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\BrokenNes-work\scspeed\wprprof.ps1"
echo.
if exist "C:\BrokenNes-work\scspeed\wpr_ingame.etl" (
  echo OK: C:\BrokenNes-work\scspeed\wpr_ingame.etl was written.
) else (
  echo FAILED: no .etl was written. Copy the messages above and send them to Claude.
)
pause
