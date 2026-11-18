@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Scripts\Build-Windows.ps1" %*
if errorlevel 1 (
  echo.
  echo Error al generar Arideev Suno Presence.
  pause
  exit /b 1
)
echo.
echo Proceso de compilacion finalizado.
pause
