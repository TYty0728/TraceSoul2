@echo off
title TraceSoul2 Host
if not defined TRACESOUL2_HOME set "TRACESOUL2_HOME=%LOCALAPPDATA%\TraceSoul2-Dev"
set TRACESOUL2_URLS=http://127.0.0.1:5080
cd /d "%~dp0..\.."
dotnet Tools\Host\bin\Debug\net8.0\TraceSoul2.Host.dll
