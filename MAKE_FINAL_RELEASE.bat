@echo off
setlocal
cd /d "%~dp0"

where powershell.exe >NUL 2>NUL
if errorlevel 1 (
  echo ERROR: Windows PowerShell is required to run the verified release pipeline.
  pause
  exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Make-Release.ps1" -InstallSdk
set "CODE=%ERRORLEVEL%"
if not "%CODE%"=="0" pause
exit /b %CODE%
