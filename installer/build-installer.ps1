<#
.SYNOPSIS
    Publishes ResourceAlerter and builds the MSI installer.

.DESCRIPTION
    Requires the WiX v5 CLI tool (`dotnet tool install --global wix --version 5.0.2`) with the
    UI extension added:
        wix extension add --global WixToolset.UI.wixext/5.0.2

    Do NOT use WiX v7+ for this project — it requires accepting the Open Source Maintenance
    Fee EULA, which has licensing implications this project intentionally avoids by pinning v5.

.PARAMETER Version
    MSI/assembly version, e.g. "1.0.0". Bump this (and keep UpgradeCode in Product.wxs fixed)
    for every release you want machines to be able to upgrade into.

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -Version 1.1.0
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\ResourceAlerter\ResourceAlerter.csproj"
$viewerProjectPath = Join-Path $repoRoot "src\ResourceAlerter.Viewer\ResourceAlerter.Viewer.csproj"
$publishDir = Join-Path $repoRoot "publish\ResourceAlerter"
$viewerPublishDir = Join-Path $repoRoot "publish\Viewer"
$outputDir = Join-Path $PSScriptRoot "bin"
$msiPath = Join-Path $outputDir "ResourceAlerterSetup-$Version.msi"

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Error "The 'wix' CLI tool was not found. Install WiX v5 with: dotnet tool install --global wix --version 5.0.2"
    exit 1
}

Write-Host "Publishing self-contained Release build ($Version)..."
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}
# Deliberately NOT single-file: native libs (SQLite, SkiaSharp) must sit as loose files next to
# the exe so they load correctly under the LocalSystem service account. See the comment in
# ResourceAlerter.csproj before changing this.
dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=false `
    -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (service) failed." }

Write-Host "Publishing Viewer..."
if (Test-Path $viewerPublishDir) {
    Remove-Item $viewerPublishDir -Recurse -Force
}
dotnet publish $viewerProjectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=false `
    -p:Version=$Version `
    -o $viewerPublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (viewer) failed." }

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

Write-Host "Building MSI ($msiPath)..."
wix build (Join-Path $PSScriptRoot "Product.wxs") `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -d "PublishDir=$publishDir" `
    -d "ViewerPublishDir=$viewerPublishDir" `
    -d "ProductVersion=$Version" `
    -o $msiPath
if ($LASTEXITCODE -ne 0) { throw "wix build failed." }

Write-Host ""
Write-Host "Done: $msiPath"
Write-Host "Test it on a target machine with: msiexec /i `"$msiPath`" /l*v install.log"
Write-Host "Silent/scripted install:          msiexec /i `"$msiPath`" /quiet /l*v install.log"
