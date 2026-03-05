<#
.SYNOPSIS
    Builds qbPortWeaver from source and produces a Chocolatey .nupkg ready for publishing.

.DESCRIPTION
    This script mirrors the CI build-release-publish.yml pipeline locally:
      1. Resolves the version from qbPortWeaver.csproj (or -Version parameter)
      2. Publishes the .NET app as a self-contained single-file win-x64 executable
      3. Builds the MSI installer using WiX Toolset v4
      4. Computes the SHA256 checksum of the local MSI
      5. Stamps the version, expected GitHub download URL, and checksum into a
         temporary copy of the choco/ package source files
      6. Runs `choco pack` to produce a .nupkg

    The choco/ source files are NOT permanently modified — all edits are written
    to a temp staging directory that is cleaned up after packing.

    WiX Toolset v4 must be installed as a .NET global tool:
      dotnet tool install --global wix --version "4.0.6"
      wix extension add WixToolset.UI.wixext/4.0.6 WixToolset.Util.wixext/4.0.6 --global

    To push the resulting .nupkg to the Chocolatey Community Repository, run:
      choco push <path-to.nupkg> --source https://push.chocolatey.org/ --api-key <key>

.PARAMETER Version
    The version string to stamp into the build (e.g. '2.3.0').
    Defaults to the version defined in qbPortWeaver.csproj.

.PARAMETER OutputDirectory
    Where to write the .nupkg file. Defaults to the repo root.

.EXAMPLE
    # Build and pack using the version from qbPortWeaver.csproj
    .\scripts\Build-ChocolateyPackage.ps1

.EXAMPLE
    # Build and pack with an explicit version override
    .\scripts\Build-ChocolateyPackage.ps1 -Version 2.3.0
#>

[CmdletBinding()]
param(
    [string] $Version         = '',
    [string] $OutputDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$chocoSrc   = Join-Path $repoRoot 'choco'
$outputDir  = if ($OutputDirectory) { $OutputDirectory } else { $repoRoot }
$stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "qbPortWeaver-choco-$(Get-Random)"

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"   -ForegroundColor Green }

# ---------------------------------------------------------------------------
# Step 1: Resolve version from csproj if not provided
# ---------------------------------------------------------------------------
Write-Step 'Resolving version...'

if (-not $Version) {
    $csprojPath = Join-Path $repoRoot 'qbPortWeaver.csproj'
    $match = Select-String -Path $csprojPath -Pattern '<Version>([^<]+)</Version>'
    if (-not $match) {
        Write-Error "Could not find <Version> in qbPortWeaver.csproj. Pass -Version explicitly."
        exit 1
    }
    $Version = $match.Matches[0].Groups[1].Value
}

$tag         = "v$Version"
$assetName   = "qbPortWeaver_${Version}_Setup.msi"
$downloadUrl = "https://github.com/martsg666/qbPortWeaver/releases/download/$tag/$assetName"

Write-Ok "Version : $Version"
Write-Ok "Tag     : $tag"

# ---------------------------------------------------------------------------
# Step 2: Publish as self-contained single-file win-x64
#         This matches the CI build-release-publish.yml publish step exactly.
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
# Step 3: Build the MSI installer using WiX Toolset v4
#         Output: installer\qbPortWeaver_{version}_Setup.msi
# ---------------------------------------------------------------------------
Write-Step 'Building MSI installer with WiX Toolset v4...'

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    Write-Host '    Installing WiX Toolset v4...' -ForegroundColor Yellow
    dotnet tool install --global wix --version "4.0.6"
    if ($LASTEXITCODE -ne 0) { Write-Error 'Failed to install WiX Toolset.'; exit 1 }
}

# Install required extensions pinned to v4 (safe to run if already present)
wix extension add WixToolset.UI.wixext/4.0.6 WixToolset.Util.wixext/4.0.6 --global
if ($LASTEXITCODE -ne 0) { Write-Error 'Failed to install WiX extensions.'; exit 1 }

$wxsFile      = Join-Path $repoRoot 'installer\qbPortWeaver.wxs'
$installerDir = Join-Path $repoRoot 'installer'
$setupMsi     = Join-Path $repoRoot "installer\qbPortWeaver_${Version}_Setup.msi"

wix build $wxsFile `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -b $installerDir `
    -d ProductVersion=$Version `
    -out $setupMsi

if ($LASTEXITCODE -ne 0) { Write-Error 'WiX build failed.'; exit 1 }

if (-not (Test-Path $setupMsi)) {
    Write-Error "Expected installer not found: $setupMsi"
    exit 1
}

Write-Ok "Installer : $setupMsi"

# ---------------------------------------------------------------------------
# Step 4: Compute SHA256 checksum of the local MSI
# ---------------------------------------------------------------------------
Write-Step 'Computing installer checksum...'

$checksum = (Get-FileHash -Path $setupMsi -Algorithm SHA256).Hash.ToUpper()

Write-Ok "SHA256    : $checksum"
Write-Ok "URL       : $downloadUrl"

# ---------------------------------------------------------------------------
# Step 5: Copy choco source to a staging directory and stamp placeholders
# Step 6: Pack the Chocolatey package
# ---------------------------------------------------------------------------
Write-Step 'Preparing and packing Chocolatey package...'

Copy-Item -Recurse -Path $chocoSrc -Destination $stagingDir
try {
    $nuspecPath  = Join-Path $stagingDir 'qbPortWeaver.nuspec'
    $installPath = Join-Path $stagingDir 'tools\chocolateyInstall.ps1'
    $verifyPath  = Join-Path $stagingDir 'tools\VERIFICATION.txt'

    (Get-Content $nuspecPath)  -replace 'TEMPLATE_VERSION',  $Version      | Set-Content $nuspecPath
    (Get-Content $installPath) -replace 'TEMPLATE_URL',      $downloadUrl `
                               -replace 'TEMPLATE_CHECKSUM', $checksum      | Set-Content $installPath
    (Get-Content $verifyPath)  -replace 'TEMPLATE_VERSION',  $Version `
                               -replace 'TEMPLATE_URL',      $downloadUrl `
                               -replace 'TEMPLATE_CHECKSUM', $checksum      | Set-Content $verifyPath

    choco pack $nuspecPath --output-directory $outputDir
    if ($LASTEXITCODE -ne 0) { Write-Error 'choco pack failed.'; exit 1 }
} finally {
    Remove-Item -Recurse -Force $stagingDir -ErrorAction SilentlyContinue
}

$nupkg = Get-Item (Join-Path $outputDir "qbportweaver.$Version.nupkg")
Write-Ok "Package   : $($nupkg.FullName)"

Write-Host "`nTo push to the Chocolatey Community Repository, run:" -ForegroundColor Yellow
Write-Host "  choco push '$($nupkg.FullName)' --source https://push.chocolatey.org/ --api-key <key>" -ForegroundColor Yellow
Write-Host "`nDone." -ForegroundColor Green
