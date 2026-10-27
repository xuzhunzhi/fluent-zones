@echo off
setlocal
cd /d "%~dp0"

rem Prefer the user-local SDK installed for this tool, fall back to PATH.
set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

echo Building FluentZones (Release, x64)...
"%DOTNET%" build "src\FluentZones.UI\FluentZones.UI.csproj" -c Release -p:Platform=x64
if errorlevel 1 (
  echo.
  echo [FAILED] Build failed. Make sure the .NET 8 SDK is installed.
  pause
  exit /b 1
)

echo.
echo Done: src\FluentZones.UI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\FluentZones.exe
pause
