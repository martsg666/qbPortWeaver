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
      6. Installs the community validation extension (idempotent) and runs
         `choco pack` — the extension hooks in and validates automatically

    The choco/ source files are NOT permanently modified — all edits are written
    to a temp staging directory that is cleaned up after packing.

    WiX Toolset v4 is installed/updated automatically by this script.
    To install manually: dotnet tool update --global wix --version "4.0.6"

    To push the resulting .nupkg to the Chocolatey Community Repository, run:
      choco push <path-to.nupkg> --source https://push.chocolatey.org/ --api-key <key>

.PARAMETER Version
    The version string to stamp into the build (e.g. '2.3.0').
    Defaults to the version defined in qbPortWeaver.csproj.

.PARAMETER OutputDirectory
    Where to write the .nupkg file. Defaults to the choco/ folder (gitignored).

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
$outputDir  = if ($OutputDirectory) { $OutputDirectory } else { $chocoSrc }
$stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "qbPortWeaver-choco-$([System.Guid]::NewGuid().ToString('N'))"

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
        throw "Could not find <Version> in qbPortWeaver.csproj. Pass -Version explicitly."
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

# Install/update WiX (idempotent — installs if missing, updates if present)
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

# ---------------------------------------------------------------------------
# Step 4: Compute SHA256 checksum of the local MSI
# ---------------------------------------------------------------------------
Write-Step 'Computing installer checksum...'

$checksum = (Get-FileHash -Path $setupMsi -Algorithm SHA256).Hash.ToUpper()

Write-Ok "SHA256    : $checksum"
Write-Ok "URL       : $downloadUrl"

# ---------------------------------------------------------------------------
# Step 5: Stage package source and stamp placeholders
# ---------------------------------------------------------------------------
Write-Step 'Staging and stamping Chocolatey package source...'

Copy-Item -Recurse -Path $chocoSrc -Destination $stagingDir
try {
    $nuspecPath  = Join-Path $stagingDir 'qbPortWeaver.nuspec'
    $installPath = Join-Path $stagingDir 'tools\chocolateyInstall.ps1'
    $verifyPath  = Join-Path $stagingDir 'tools\VERIFICATION.txt'

    (Get-Content $nuspecPath  -Encoding utf8) -replace 'TEMPLATE_VERSION',  $Version      | Set-Content $nuspecPath  -Encoding utf8
    (Get-Content $installPath -Encoding utf8) -replace 'TEMPLATE_URL',      $downloadUrl `
                               -replace 'TEMPLATE_CHECKSUM', $checksum      | Set-Content $installPath -Encoding utf8
    (Get-Content $verifyPath  -Encoding utf8) -replace 'TEMPLATE_VERSION',  $Version `
                               -replace 'TEMPLATE_URL',      $downloadUrl `
                               -replace 'TEMPLATE_CHECKSUM', $checksum      | Set-Content $verifyPath  -Encoding utf8

    Write-Ok "Placeholders stamped"

    # Verify no TEMPLATE_ placeholders survived the substitution
    $unreplaced = $false
    foreach ($f in @($nuspecPath, $installPath, $verifyPath)) {
        if (Select-String -Path $f -Pattern 'TEMPLATE_' -Quiet) {
            Write-Host "    Unreplaced TEMPLATE_ placeholder in: $f" -ForegroundColor Red
            $unreplaced = $true
        }
    }
    if ($unreplaced) { throw 'Unreplaced TEMPLATE_ placeholders found — stamping failed.' }

    # ---------------------------------------------------------------------------
    # Step 6: Install community validation extension + pack
    #         The extension hooks into choco pack and runs validation rules
    #         automatically — no separate validate command needed in v2.x.
    # ---------------------------------------------------------------------------
    Write-Step 'Installing community validation extension and packing...'

    # Install the community validation extension if not already present (idempotent).
    # When installed, it automatically validates the package during choco pack.
    choco install chocolatey-community-validation.extension -y --no-progress
    if ($LASTEXITCODE -ne 0) { throw 'Failed to install chocolatey-community-validation.extension.' }

    choco pack $nuspecPath --output-directory $outputDir
    if ($LASTEXITCODE -ne 0) { throw 'choco pack failed.' }
} finally {
    Remove-Item -Recurse -Force $stagingDir -ErrorAction SilentlyContinue
}

$nupkg = Get-Item (Join-Path $outputDir "qbportweaver.$Version.nupkg")
Write-Ok "Package   : $($nupkg.FullName)"

Write-Host "`nTo push to the Chocolatey Community Repository, run:" -ForegroundColor Yellow
Write-Host "  choco push '$($nupkg.FullName)' --source https://push.chocolatey.org/ --api-key <key>" -ForegroundColor Yellow
Write-Host "`nDone." -ForegroundColor Green
