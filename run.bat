@echo off
REM Cephalon Cuda - quick dev build + launch (Debug, framework-dependent).
REM For the sharable single-file exe, use publish.bat instead.

setlocal
cd /d "%~dp0"

echo Building (Debug) ...
dotnet build "src\CephalonCuda\CephalonCuda.csproj" -v q -nologo
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo BUILD FAILED - see errors above.
    pause
    exit /b 1
)

echo Launching ...
start "" "src\CephalonCuda\bin\Debug\net8.0-windows10.0.19041.0\CephalonCuda.exe"
endlocal
