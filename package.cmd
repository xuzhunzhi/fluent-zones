@echo off
setlocal
cd /d "%~dp0"

set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

if exist dist rmdir /s /q dist

echo Publishing self-contained package (win-x64)...
"%DOTNET%" publish "src\WindowLayout.UI\WindowLayout.UI.csproj" -c Release -p:Platform=x64 ^
  -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:EnableMsixTooling=true ^
  -p:DebugType=none -p:DebugSymbols=false -o "dist\WindowLayout"
if errorlevel 1 (
  echo.
  echo [FAILED] Publish failed. Close the running app first (Windows locks the exe/dll).
  pause
  exit /b 1
)

rem dotnet publish 不会带上编译后的 XAML（.xbf），从 build 输出里补过去
copy /y "src\WindowLayout.UI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\*.xbf" "dist\WindowLayout\" >nul
if errorlevel 1 (
  echo [FAILED] Missing .xbf files. Run build.cmd once, then package.cmd again.
  pause
  exit /b 1
)

echo Zipping...
powershell -NoProfile -Command "Compress-Archive -Path 'dist\WindowLayout\*' -DestinationPath 'dist\WindowLayout-v0.1.0-win-x64.zip' -Force"

echo.
echo Done: dist\WindowLayout-v0.1.0-win-x64.zip
dir /b "dist\WindowLayout-v0.1.0-win-x64.zip"
pause
