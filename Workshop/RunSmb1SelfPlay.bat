@echo off
setlocal

rem Launches SMB1 self-play: starts the NESReflex Python inference server (the source
rem ML_NesPlayer project's real, unmodified server), waits for it to load the model, then
rem starts BrokenNes Workshop's --selfplay client against it. See Workshop/README.md's
rem "Run (SMB1 self-play)" section for details on what this is actually doing.

set "ML_ROOT=C:\Users\philt\OneDrive\Documents\PROJECTS\!!! Experiments\ML_NesPlayer"
set "ROM=%ML_ROOT%\TAS\Nintendo Entertainment System\Super Mario Bros. (JU) (PRG0) [!].nes"
set "CHECKPOINT=%ML_ROOT%\data\checkpoints\overnight_ppu_v2\epoch_0001.pt"
set "METADATA=%ML_ROOT%\data\metadata_ppu_v2\samples.parquet"
set "PYTHON=%ML_ROOT%\.venv\Scripts\python.exe"
set "WORKSHOP_EXE=%~dp0bin\Release\net10.0-windows\BrokenNes.Workshop.exe"
set "OUT_DIR=%~dp0SelfPlayOutput"

if not exist "%OUT_DIR%\checkpoints" mkdir "%OUT_DIR%\checkpoints"
if not exist "%OUT_DIR%\screenshots" mkdir "%OUT_DIR%\screenshots"

echo Starting NESReflex inference server...
start "NESReflex Inference Server" cmd /k ""%PYTHON%" "%ML_ROOT%\scripts\nesreflex_inference_server.py" --checkpoint "%CHECKPOINT%" --metadata "%METADATA%" --protocol v2 --device cpu"

echo Waiting for the model to load...
rem (ping-based delay instead of `timeout`, which errors out under redirected stdin)
ping -n 11 127.0.0.1 >nul

echo Starting BrokenNes Workshop self-play...
start "BrokenNes SMB1 Self-Play" cmd /k ""%WORKSHOP_EXE%" --selfplay --rom "%ROM%" --frames 2000000000 --log-every 150 --auto-start-frame 60 --checkpoint-dir "%OUT_DIR%\checkpoints" --screenshot-every 300 --screenshot-dir "%OUT_DIR%\screenshots""

endlocal
