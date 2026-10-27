@echo off
setlocal
cd /d "%~dp0"

set "DOTNET=%USERPROFILE%\.dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

if exist dist rmdir /s /q dist

echo Publishing self-contained package (win-x64)...
"%DOTNET%" publish "src\FluentZones.UI\FluentZones.UI.csproj" -c Release -p:Platform=x64 ^
  -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:EnableMsixTooling=true ^
  -p:DebugType=none -p:DebugSymbols=false -o "dist\FluentZones"
if errorlevel 1 (
  echo.
  echo [FAILED] Publish failed. Close the running app first (Windows locks the exe/dll).
  pause
  exit /b 1
)

rem dotnet publish 不会带上编译后的 XAML（.xbf），从 build 输出里补过去
copy /y "src\FluentZones.UI\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\*.xbf" "dist\FluentZones\" >nul
if errorlevel 1 (
  echo [FAILED] Missing .xbf files. Run build.cmd once, then package.cmd again.
  pause
  exit /b 1
)

rem publish 会把 bin 里的 runtimeconfig 改成自包含，这里重编一次恢复成框架依赖，避免之后 run.cmd 报找不到 .NET
"%DOTNET%" build "src\FluentZones.UI\FluentZones.UI.csproj" -c Release -p:Platform=x64 >nul
if errorlevel 1 (
  echo [WARN] Failed to restore the normal build output in bin. Run build.cmd manually.
)

echo Zipping...
powershell -NoProfile -Command "Compress-Archive -Path 'dist\FluentZones\*' -DestinationPath 'dist\fluent-zones-v0.1.0-win-x64.zip' -Force"

echo.
echo Done: dist\fluent-zones-v0.1.0-win-x64.zip
dir /b "dist\fluent-zones-v0.1.0-win-x64.zip"
pause
