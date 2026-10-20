@echo off
rem Double-click me. Starts the setup wizard (setup\Wizard.ps1) without changing any Windows settings.
title omae-wa-mou-debloated
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup\Wizard.ps1" %*
echo.
pause
