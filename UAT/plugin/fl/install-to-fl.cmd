@echo off
rem Installs the latest BrokenNes2 plugin build into FL Studio 2026 (one UAC prompt). Close FL first.
rem Uses Windows PowerShell (always present), so it works from cmd, PowerShell or a double-click.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-to-fl.ps1"
echo.
pause
