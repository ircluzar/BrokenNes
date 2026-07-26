@echo off
setlocal

rem Launches SMB1 self-play with a real, visible emulator window: starts the NESReflex Python
rem inference server (the source ML_NesPlayer project's real, unmodified server), waits for it
rem to load the model, then opens BrokenNes Workshop's interactive window. Once it's open, check
rem the "Self-Play (SMB1)" box (auto-loads the ROM and connects to the server), click the game
rem screen, and press Enter to press Start - the model can never press Start itself, matching the
rem source project's design, so a human always has to kick off the first run. See
rem Workshop/README.md's "Run (SMB1 self-play)" section for details on what this is actually doing.
rem
rem For a headless/scripted run instead of a visible window, use BrokenNes.Workshop.exe --selfplay
rem directly (see the README).

set "ML_ROOT=C:\Users\philt\OneDrive\Documents\PROJECTS\!!! Experiments\ML_NesPlayer"
set "CHECKPOINT=%ML_ROOT%\data\checkpoints\overnight_ppu_v2\epoch_0001.pt"
set "METADATA=%ML_ROOT%\data\metadata_ppu_v2\samples.parquet"
set "PYTHON=%ML_ROOT%\.venv\Scripts\python.exe"
set "WORKSHOP_EXE=%~dp0bin\Release\net10.0-windows\BrokenNes.Workshop.exe"

echo Starting NESReflex inference server...
rem --device cuda: measured ~101 fps vs ~17 fps on CPU for this model - CPU inference alone was
rem the bottleneck keeping self-play well under real NES speed. Falls back to --device cpu below
rem if no CUDA GPU is available on this machine.
start "NESReflex Inference Server" cmd /k ""%PYTHON%" "%ML_ROOT%\scripts\nesreflex_inference_server.py" --checkpoint "%CHECKPOINT%" --metadata "%METADATA%" --protocol v2 --device cuda"

echo Waiting for the model to load...
rem (ping-based delay instead of `timeout`, which errors out under redirected stdin)
ping -n 11 127.0.0.1 >nul

echo Opening BrokenNes Workshop...
start "" "%WORKSHOP_EXE%"

endlocal
