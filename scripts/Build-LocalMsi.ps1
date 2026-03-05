<#
.SYNOPSIS
    Builds a local qbPortWeaver MSI installer for testing purposes.

.DESCRIPTION
    Mirrors the CI build-release-publish.yml pipeline locally:
      1. Publishes the .NET app as a self-contained single-file win-x64 executable
      2. Builds the MSI installer using WiX Toolset v4

    Use this script to build the MSI locally — a regular Visual Studio
    Release build does NOT produce the self-contained single-file output that
    the WiX source expects under bin\Release\<tfm>\win-x64\publish\.

    WiX Toolset v4 is installed/updated automatically by this script.
    To install manually: dotnet tool update --global wix --version "4.0.6"

.PARAMETER Version
    The version string to stamp into the build (e.g. '2.3.0').
    Defaults to the version defined in qbPortWeaver.csproj.

.EXAMPLE
    # Build using the default version from qbPortWeaver.csproj
    .\scripts\Build-LocalMsi.ps1

.EXAMPLE
    # Build with an explicit version override
    .\scripts\Build-LocalMsi.ps1 -Version 2.3.0
#>

[CmdletBinding()]
param(
    [string] $Version = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"   -ForegroundColor Green }

# ---------------------------------------------------------------------------
# Step 1: Resolve version — read from qbPortWeaver.csproj if not provided
# ---------------------------------------------------------------------------
Write-Step 'Resolving version...'

if (-not $Version) {
    $csprojPath = Join-Path $repoRoot 'qbPortWeaver.csproj'
    $match = Select-String -Path $csprojPath -Pattern '<Version>([^<]+)</Version>'
    if (-not $match) {
        throw "Could not find <Version> in qbPortWeaver.csproj. Pass -Version explicitly."
    }
    $Version = $match.Matches[0].Groups[1].Value
}

Write-Ok "Version : $Version"

# ---------------------------------------------------------------------------
# Step 2: Publish as self-contained single-file win-x64
#         This matches the CI build-release-publish.yml publish step exactly.
#         Output lands in: bin\Release\<tfm>\win-x64\publish\
# ---------------------------------------------------------------------------
Write-Step 'Publishing self-contained single-file executable...'

Push-Location $repoRoot
try {
    dotnet publish qbPortWeaver.csproj `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:Version=$Version `
        -p:FileVersion="$Version.0" `
        -p:AssemblyVersion="$Version.0"

    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
} finally {
    Pop-Location
}

$tfm          = ([xml](Get-Content (Join-Path $repoRoot 'qbPortWeaver.csproj'))).Project.PropertyGroup.TargetFramework
$publishedExe = Join-Path $repoRoot "bin\Release\$tfm\win-x64\publish\qbPortWeaver.exe"
if (-not (Test-Path $publishedExe)) {
    throw "Expected publish output not found: $publishedExe"
}

Write-Ok "Published : $publishedExe"

# ---------------------------------------------------------------------------
# Step 3: Build the MSI installer using WiX Toolset v4
#         Output: installer\qbPortWeaver_{version}_Setup.msi
# ---------------------------------------------------------------------------
Write-Step 'Building MSI installer with WiX Toolset v4...'

# Ensure WiX is installed (update is idempotent — installs if missing, updates if present)
dotnet tool update --global wix --version "4.0.6"
if ($LASTEXITCODE -ne 0) { throw 'Failed to install/update WiX Toolset.' }

# Install required extensions pinned to v4 (safe to run if already present)
wix extension add WixToolset.UI.wixext/4.0.6 WixToolset.Util.wixext/4.0.6 --global
if ($LASTEXITCODE -ne 0) { throw 'Failed to install WiX extensions.' }

$wxsFile      = Join-Path $repoRoot 'installer\qbPortWeaver.wxs'
$installerDir = Join-Path $repoRoot 'installer'
$setupMsi     = Join-Path $repoRoot "installer\qbPortWeaver_${Version}_Setup.msi"

wix build $wxsFile `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -b $installerDir `
    -d ProductVersion=$Version `
    -d TFM=$tfm `
    -out $setupMsi

if ($LASTEXITCODE -ne 0) { throw 'WiX build failed.' }

if (-not (Test-Path $setupMsi)) {
    throw "Expected installer not found: $setupMsi"
}

Write-Ok "Installer : $setupMsi"
Write-Host "`nDone." -ForegroundColor Green
