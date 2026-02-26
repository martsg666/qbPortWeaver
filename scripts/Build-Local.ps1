<#
.SYNOPSIS
    Builds a local qbPortWeaver installer for testing purposes.

.DESCRIPTION
    Mirrors the CI build-release workflow locally:
      1. Publishes the .NET app as a self-contained single-file win-x64 executable
      2. Compiles the NSIS installer using the published output

    Use this script before running makensis locally — a regular Visual Studio
    Release build does NOT produce the self-contained single-file output that
    the NSIS script expects under bin\Release\net10.0-windows\win-x64\publish\.

.PARAMETER Version
    The version string to stamp into the build (e.g. '2.2.0').
    Defaults to the version defined in AppConstants.cs.

.EXAMPLE
    # Build using the default version from AppConstants.cs
    .\scripts\Build-Local.ps1

.EXAMPLE
    # Build with an explicit version override
    .\scripts\Build-Local.ps1 -Version 2.2.0
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
# Step 1: Resolve version — read from AppConstants.cs if not provided
# ---------------------------------------------------------------------------
Write-Step 'Resolving version...'

if (-not $Version) {
    $constantsPath = Join-Path $repoRoot 'AppConstants.cs'
    $match = Select-String -Path $constantsPath -Pattern 'APP_VERSION\s*=\s*"([^"]+)"'
    if (-not $match) {
        Write-Error "Could not find APP_VERSION in AppConstants.cs. Pass -Version explicitly."
        exit 1
    }
    $Version = $match.Matches[0].Groups[1].Value
}

Write-Ok "Version : $Version"

# ---------------------------------------------------------------------------
# Step 2: Publish as self-contained single-file win-x64
#         This matches the CI build-release.yml publish step exactly.
#         Output lands in: bin\Release\net10.0-windows\win-x64\publish\
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

    if ($LASTEXITCODE -ne 0) { Write-Error 'dotnet publish failed.'; exit 1 }
} finally {
    Pop-Location
}

$publishedExe = Join-Path $repoRoot "bin\Release\net10.0-windows\win-x64\publish\qbPortWeaver.exe"
if (-not (Test-Path $publishedExe)) {
    Write-Error "Expected publish output not found: $publishedExe"
    exit 1
}

Write-Ok "Published : $publishedExe"

# ---------------------------------------------------------------------------
# Step 3: Compile the NSIS installer
#         Must run from inside installer\ so relative paths in the .nsi
#         script (..\ for bin output, icons, etc.) resolve correctly.
#         Output: installer\qbPortWeaver_{version}_Setup.exe
# ---------------------------------------------------------------------------
Write-Step 'Compiling NSIS installer...'

# Resolve makensis — check PATH first, then fall back to the default install location
$makensis = 'makensis'
if (-not (Get-Command makensis -ErrorAction SilentlyContinue)) {
    $fallback = "${env:ProgramFiles(x86)}\NSIS\makensis.exe"
    if (Test-Path $fallback) {
        $makensis = $fallback
        Write-Ok "Found makensis at: $makensis"
    } else {
        Write-Error "makensis not found. Install NSIS from https://nsis.sourceforge.io/"
        exit 1
    }
}

Push-Location (Join-Path $repoRoot 'installer')
try {
    & $makensis /DPRODUCT_VERSION=$Version qbPortWeaverSetup.nsi
    if ($LASTEXITCODE -ne 0) { Write-Error 'makensis failed.'; exit 1 }
} finally {
    Pop-Location
}

$setupExe = Join-Path $repoRoot "installer\qbPortWeaver_${Version}_Setup.exe"
if (-not (Test-Path $setupExe)) {
    Write-Error "Expected installer not found: $setupExe"
    exit 1
}

Write-Ok "Installer : $setupExe"
Write-Host "`nDone." -ForegroundColor Green
