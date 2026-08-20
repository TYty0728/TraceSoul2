@echo off
setlocal
set "TRACESOUL2_HOME=%~dp0..\Data"
set "TRACESOUL2_PLUGINS=%~dp0..\Plugins"
set "TRACESOUL2_RESTART_MODE=supervisor"

:run
"%~dp0TraceSoul2.Host.exe"
if errorlevel 1 dotnet "%~dp0TraceSoul2.Host.dll"
timeout /t 5 /nobreak >nul
goto run
