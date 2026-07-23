@echo off
setlocal
cd /d "%~dp0"
call MAKE_FINAL_RELEASE.bat
exit /b %ERRORLEVEL%
