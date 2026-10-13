# Builds the volunteer capture kit: a self-contained LockpickCapture.exe (no .NET install needed) + README,
# zipped into artifacts/. Works with Windows PowerShell or pwsh:
#   powershell -ExecutionPolicy Bypass -File tools/capture-kit/build-capture-kit.ps1 [-Version 0.1.1] [-ZipName LockpickCapture.zip]
param(
    [string]$Version = ([xml](Get-Content "$PSScriptRoot/../../Directory.Build.props")).Project.PropertyGroup.Version,
    [string]$OutDir = "$PSScriptRoot/../../artifacts",
    # Stable name for the rolling download link; defaults to a versioned name.
    [string]$ZipName = ""
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot/../.."
$staging = Join-Path $OutDir "capture-kit-staging"
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $staging | Out-Null

dotnet publish "$root/tools/Lockpick.Probe" -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $staging
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# The kit's exe name switches the probe into double-click capture mode.
Move-Item (Join-Path $staging 'lockpick-probe.exe') (Join-Path $staging 'LockpickCapture.exe') -Force
Copy-Item "$PSScriptRoot/README.txt" $staging

if (-not $ZipName) { $ZipName = "LockpickCapture-$Version.zip" }
$zip = Join-Path $OutDir $ZipName
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
Remove-Item $staging -Recurse -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Host "Capture kit: $zip"
Write-Host "SHA-256:     $hash"
