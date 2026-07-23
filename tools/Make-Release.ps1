[CmdletBinding()]
param(
    [switch]$InstallSdk,
    [switch]$SkipLiveVerification,
    [string]$DeliveryRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Project = Join-Path $Root 'src\CephalonCuda\CephalonCuda.csproj'
$Publish = Join-Path $Root 'publish'
$Dist = if ($DeliveryRoot) { [IO.Path]::GetFullPath($DeliveryRoot) } else { Join-Path $Root 'dist' }
$Public = Join-Path $Dist 'Cephalon Cuda 2.2 - Nexus Release'
$Source = Join-Path $Dist 'Cephalon Cuda 2.2 - Source'
$CombinedZip = Join-Path $Dist 'Cephalon_Cuda_2.2_Final_Delivery.zip'
$PublicZip = Join-Path $Dist 'Cephalon_Cuda_2.2_Nexus_Release.zip'
$SourceZip = Join-Path $Dist 'Cephalon_Cuda_2.2_Source.zip'

function Write-Step([string]$Text) {
    Write-Host "`n== $Text ==" -ForegroundColor Cyan
}

function Assert-LastExit([string]$Action) {
    if ($LASTEXITCODE -ne 0) { throw "$Action failed with exit code $LASTEXITCODE." }
}

function Get-DotNet8 {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $cmd) { return $false }
    $sdks = & dotnet --list-sdks
    return [bool]($sdks | Where-Object { $_ -match '^8\.' })
}

Write-Step 'Checking Windows and .NET 8 SDK'
if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    throw 'This WPF release pipeline must run on 64-bit Windows 10 or Windows 11.'
}

if (-not (Get-DotNet8)) {
    if (-not $InstallSdk) {
        throw 'The .NET 8 SDK is missing. Re-run MAKE_FINAL_RELEASE.bat or install Microsoft.DotNet.SDK.8.'
    }
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw 'The .NET 8 SDK is missing and winget is unavailable. Install the official .NET 8 SDK, then retry.'
    }
    & winget install --id Microsoft.DotNet.SDK.8 --exact --accept-source-agreements --accept-package-agreements
    Assert-LastExit 'SDK installation'
    $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH', 'User')
    if (-not (Get-DotNet8)) { throw 'A .NET 8 SDK was not detected after installation.' }
}

Write-Step 'Cleaning prior build output'
Get-Process CephalonCuda -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
foreach ($path in @($Publish, (Join-Path $Root 'src\CephalonCuda\bin'), (Join-Path $Root 'src\CephalonCuda\obj'))) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

Write-Step 'Restoring exact project dependencies'
& dotnet restore $Project -r win-x64 --nologo
Assert-LastExit 'dotnet restore'

Write-Step 'Compiling Release with every warning treated as an error'
& dotnet build $Project -c Release -r win-x64 --no-restore --nologo -p:TreatWarningsAsErrors=true
Assert-LastExit 'dotnet build'

Write-Step 'Publishing a self-contained single-file Windows executable'
& dotnet publish $Project -c Release -r win-x64 --self-contained true --no-restore --nologo `
    -p:TreatWarningsAsErrors=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -o $Publish
Assert-LastExit 'dotnet publish'

$Exe = Join-Path $Publish 'CephalonCuda.exe'
if (-not (Test-Path $Exe)) { throw 'Publish completed without producing CephalonCuda.exe.' }

Write-Step 'Running the complete UI smoke cycle'
& $Exe --smoke
Assert-LastExit 'UI smoke test'

if (-not $SkipLiveVerification) {
    Write-Step 'Verifying schema, migration, official drops, market, world state, relics and retrieval'
    & $Exe --verify-data
    Assert-LastExit 'live-data verification'
}

Write-Step 'Creating the two final delivery folders'
if (Test-Path $Dist) { Remove-Item $Dist -Recurse -Force }
New-Item -ItemType Directory -Path $Public | Out-Null
New-Item -ItemType Directory -Path $Source | Out-Null

Copy-Item $Exe (Join-Path $Public 'CephalonCuda.exe')
foreach ($file in @('NEXUS_README.txt','NEXUS_DESCRIPTION_BBCODE.txt','CHANGELOG.md','MAJOR_UI_REWORK_REPORT.md','VALIDATION_REPORT.md','PRIVACY_AND_DATA.md','THIRD_PARTY_NOTICES.md')) {
    Copy-Item (Join-Path $Root $file) (Join-Path $Public $file)
}

$robocopyArgs = @(
    $Root, $Source, '/E', '/R:2', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NP',
    '/XD', '.git', '.github\workflows\artifacts', '.vs', 'bin', 'obj', 'publish', 'dist',
    '/XF', '*.db', '*.sqlite', '*.sqlite3', '*.log', '*.user', '*.suo', '*.pfx', '*.snk'
)
& robocopy @robocopyArgs | Out-Null
if ($LASTEXITCODE -gt 7) { throw "Source-copy step failed with robocopy exit code $LASTEXITCODE." }

Write-Step 'Creating ZIP archives and SHA-256 manifests'
foreach ($zip in @($CombinedZip, $PublicZip, $SourceZip)) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
}
Compress-Archive -Path $Public -DestinationPath $PublicZip -CompressionLevel Optimal
Compress-Archive -Path $Source -DestinationPath $SourceZip -CompressionLevel Optimal
Compress-Archive -Path $Public,$Source -DestinationPath $CombinedZip -CompressionLevel Optimal

$hashes = @($Exe, $PublicZip, $SourceZip, $CombinedZip) | ForEach-Object {
    $h = Get-FileHash $_ -Algorithm SHA256
    "{0}  {1}" -f $h.Hash.ToLowerInvariant(), (Split-Path $_ -Leaf)
}
$hashes | Set-Content (Join-Path $Dist 'SHA256SUMS.txt') -Encoding ASCII

Write-Host "`nVERIFIED RELEASE COMPLETE" -ForegroundColor Green
Write-Host "Nexus folder : $Public"
Write-Host "Source folder: $Source"
Write-Host "Combined ZIP : $CombinedZip"
