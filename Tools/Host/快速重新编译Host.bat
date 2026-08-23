@echo off
REM This .bat must stay ANSI/GBK. Do not save as UTF-8.
setlocal
cd /d "%~dp0"
title TraceSoul2 Host 快速重新编译

echo 正在重新编译 TraceSoul2 Host...
echo.
dotnet build "%~dp0TraceSoul2.Host.csproj" --no-restore --nologo
if errorlevel 1 goto FAIL

echo.
echo [完成] TraceSoul2 Host 已重新编译。
goto END

:FAIL
echo.
echo [失败] Host 编译失败。
echo 如果提示文件被占用，请先关闭正在运行的 TraceSoul2 Host，再双击本文件。

:END
echo.
pause
