@echo off
setlocal
cd /d "%~dp0"

rem Build portable zip + MSIX + EXE installer into dist\.
rem Options are passed through to build-release.ps1, e.g.: package-all.cmd -SkipMsix
powershell -NoProfile -ExecutionPolicy Bypass -File "packaging\build-release.ps1" %*
if errorlevel 1 (
  echo.
  echo [FAILED] packaging failed, see the error above.
  pause
  exit /b 1
)

echo.
pause
