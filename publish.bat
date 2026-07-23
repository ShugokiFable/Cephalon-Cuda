@echo off
REM Cephalon Cuda 2.2 strict single-file publish shortcut.
REM For the complete two-folder delivery, run MAKE_FINAL_RELEASE.bat.
setlocal
cd /d "%~dp0"

where dotnet >NUL 2>NUL
if errorlevel 1 (
  echo ERROR: The .NET 8 SDK is required. Run BUILD_V2.2.bat for automatic setup.
  if /I not "%~1"=="/nopause" pause
  exit /b 1
)

if exist ".\publish" rmdir /S /Q ".\publish"
dotnet restore "src\CephalonCuda\CephalonCuda.csproj" -r win-x64 --nologo
if errorlevel 1 goto :failed

dotnet build "src\CephalonCuda\CephalonCuda.csproj" -c Release -r win-x64 --no-restore --nologo -p:TreatWarningsAsErrors=true
if errorlevel 1 goto :failed

dotnet publish "src\CephalonCuda\CephalonCuda.csproj" -c Release -r win-x64 --self-contained true --no-restore --nologo ^
  -p:TreatWarningsAsErrors=true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=embedded ^
  -o ".\publish"
if errorlevel 1 goto :failed

".\publish\CephalonCuda.exe" --smoke
if errorlevel 1 goto :failed

echo SUCCESS: .\publish\CephalonCuda.exe passed compilation and smoke testing.
if /I not "%~1"=="/nopause" pause
exit /b 0

:failed
echo.
echo BUILD FAILED. No public package was created.
if /I not "%~1"=="/nopause" pause
exit /b 1
