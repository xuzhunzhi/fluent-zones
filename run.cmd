@echo off
setlocal
cd /d "%~dp0"

set "EXE=src\FluentZones.UI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\FluentZones.exe"
if not exist "%EXE%" set "EXE=src\FluentZones.UI\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\FluentZones.exe"
if not exist "%EXE%" (
  echo Not built yet. Run build.cmd first.
  pause
  exit /b 1
)

start "" "%EXE%"
