@echo off
rem AgentLegion launcher. Starts the web app on localhost only and opens the browser.
rem   run.bat          -> http://localhost:5165
rem   run.bat 6000     -> http://localhost:6000
setlocal
rem legion.ps1 and the settings (legion.json, jobs.json, logs) live next to this file, so always run from here.
cd /d "%~dp0"

set "PORT=%~1"
if "%PORT%"=="" set "PORT=5165"

if not exist "%~dp0AgentLegion.exe" (
  echo AgentLegion.exe was not found next to run.bat.
  pause
  exit /b 1
)

echo.
echo   AgentLegion  ^>  http://localhost:%PORT%
echo   Close this window or press Ctrl+C to stop. Stopping also ends every running job session.
echo.

rem open the browser a few seconds from now, after the server is listening
start "" /b cmd /c "ping -n 4 127.0.0.1 >nul & start "" http://localhost:%PORT%"

rem full path on purpose: with NoDefaultCurrentDirectoryInExePath set (hardened PCs) cmd does not look in the current folder
"%~dp0AgentLegion.exe" --urls http://localhost:%PORT%
echo.
echo AgentLegion stopped (exit code %ERRORLEVEL%).
pause
