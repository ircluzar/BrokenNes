@echo off
rem DEVELOPER SHORTCUT (FL Studio 2026 only, Plugin\dist build). The standard way to install is the desktop app:
rem   Config > Synthesizer Mode > Install to FL Studio...   or   BrokenNes.Windows.exe --install-vst
rem which asks which FL Studio (several can be installed) and shows the same UAC prompt.
rem Installs the latest BrokenNes2 plugin build into FL Studio 2026 (one UAC prompt). Close FL first.
rem Uses Windows PowerShell (always present), so it works from cmd, PowerShell or a double-click.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-to-fl.ps1"
echo.
pause
