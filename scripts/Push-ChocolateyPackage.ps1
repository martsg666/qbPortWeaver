<#
.SYNOPSIS
    Packages and (optionally) pushes the latest qbPortWeaver GitHub release
    to the Chocolatey Community Repository.

.DESCRIPTION
    This script:
      1. Queries the GitHub API for the latest release of qbPortWeaver
      2. Downloads the Setup.exe asset
      3. Computes its SHA256 checksum
      4. Stamps the version, URL, and checksum into the choco/ package source files
      5. Runs `choco pack` to produce a .nupkg
      6. Optionally runs `choco push` to publish to the CCR

    The script works on copies of the choco/ source files and does NOT
    permanently modify them — all edits are written to a temp staging directory.

.PARAMETER ApiKey
    Your Chocolatey Community Repository API key.
    If omitted the script packs only (no push).
    You can also set the environment variable CHOCOLATEY_API_KEY instead.

.PARAMETER Tag
    A specific release tag to package (e.g. 'v2.1.0').
    Defaults to the latest published release.

.PARAMETER OutputDirectory
    Where to write the .nupkg file.  Defaults to the repo root.

.EXAMPLE
    # Pack the latest release without pushing
    .\scripts\Push-ChocolateyPackage.ps1

.EXAMPLE
    # Pack and push the latest release
    .\scripts\Push-ChocolateyPackage.ps1 -ApiKey 'your-api-key-here'

.EXAMPLE
    # Pack and push a specific release tag
    .\scripts\Push-ChocolateyPackage.ps1 -Tag v2.0.0 -ApiKey 'your-api-key-here'
#>

[CmdletBinding()]
param(
    [string] $ApiKey        = $env:CHOCOLATEY_API_KEY,
    [string] $Tag           = '',
    [string] $OutputDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Resolve paths relative to the repository root (script lives in scripts/)
# ---------------------------------------------------------------------------
$repoRoot   = Split-Path -Parent $PSScriptRoot
$chocoSrc   = Join-Path $repoRoot 'choco'
$outputDir  = if ($OutputDirectory) { $OutputDirectory } else { $repoRoot }
$stagingDir = Join-Path ([System.IO.Path]::GetTempPath()) "qbPortWeaver-choco-$(Get-Random)"

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    $msg"   -ForegroundColor Green }

# ---------------------------------------------------------------------------
# Step 1: Resolve release information from GitHub API
# ---------------------------------------------------------------------------
Write-Step 'Querying GitHub for release info...'

$headers = @{ 'User-Agent' = 'qbPortWeaver-choco-packager' }

if ($Tag) {
    $apiUrl = "https://api.github.com/repos/martsg666/qbPortWeaver/releases/tags/$Tag"
} else {
    $apiUrl = 'https://api.github.com/repos/martsg666/qbPortWeaver/releases/latest'
}

$release = Invoke-RestMethod -Uri $apiUrl -Headers $headers
$relTag  = $release.tag_name
$version = $relTag.TrimStart('v')

Write-Ok "Tag     : $relTag"
Write-Ok "Version : $version"

# ---------------------------------------------------------------------------
# Step 2: Locate the Setup.exe asset
# ---------------------------------------------------------------------------
$assetName = "qbPortWeaver_"+$version+"_Setup.exe"
$asset     = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1

if (-not $asset) {
    $available = ($release.assets | Select-Object -ExpandProperty name) -join ', '
    Write-Error "Asset '$assetName' not found in release $relTag. Available assets: $available"
    exit 1
}

$downloadUrl = $asset.browser_download_url
Write-Ok "Asset   : $downloadUrl"

# ---------------------------------------------------------------------------
# Step 3: Download asset and compute checksum
# ---------------------------------------------------------------------------
Write-Step 'Downloading release asset and computing checksum...'

$tmpExe = Join-Path ([System.IO.Path]::GetTempPath()) $assetName
Invoke-WebRequest -Uri $downloadUrl -OutFile $tmpExe -UseBasicParsing
$checksum = (Get-FileHash -Path $tmpExe -Algorithm SHA256).Hash.ToUpper()
Remove-Item $tmpExe -Force

Write-Ok "SHA256  : $checksum"

# ---------------------------------------------------------------------------
# Step 4: Copy package source to a staging directory and stamp placeholders
# ---------------------------------------------------------------------------
Write-Step 'Preparing staging directory...'

Copy-Item -Recurse -Path $chocoSrc -Destination $stagingDir

$nuspecPath  = Join-Path $stagingDir 'qbPortWeaver.nuspec'
$installPath = Join-Path $stagingDir 'tools\chocolateyInstall.ps1'
$verifyPath  = Join-Path $stagingDir 'tools\VERIFICATION.txt'

(Get-Content $nuspecPath)  -replace 'TEMPLATE_VERSION',  $version      | Set-Content $nuspecPath
(Get-Content $installPath) -replace 'TEMPLATE_URL',      $downloadUrl `
                           -replace 'TEMPLATE_CHECKSUM', $checksum      | Set-Content $installPath
(Get-Content $verifyPath)  -replace 'TEMPLATE_VERSION',  $version `
                           -replace 'TEMPLATE_URL',      $downloadUrl `
                           -replace 'TEMPLATE_CHECKSUM', $checksum      | Set-Content $verifyPath

Write-Ok "Staged to: $stagingDir"

# ---------------------------------------------------------------------------
# Step 5: Pack
# ---------------------------------------------------------------------------
Write-Step 'Running choco pack...'

choco pack $nuspecPath --output-directory $outputDir
if ($LASTEXITCODE -ne 0) { Write-Error 'choco pack failed.'; exit 1 }

$nupkg = Get-Item (Join-Path $outputDir "qbportweaver.$version.nupkg") -ErrorAction SilentlyContinue
if (-not $nupkg) {
    $nupkg = Get-Item (Join-Path $outputDir '*.nupkg') | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

Write-Ok "Created : $($nupkg.FullName)"

# ---------------------------------------------------------------------------
# Step 6: Push (optional)
# ---------------------------------------------------------------------------
if ($ApiKey) {
    Write-Step 'Pushing to Chocolatey Community Repository...'
    choco push $nupkg.FullName --source https://push.chocolatey.org/ --api-key $ApiKey
    if ($LASTEXITCODE -ne 0) { Write-Error 'choco push failed.'; exit 1 }
    Write-Ok 'Package submitted. It will appear on chocolatey.org after moderation.'
} else {
    Write-Host "`n[INFO] No API key provided — skipping push." -ForegroundColor Yellow
    Write-Host "       To push, run:"
    Write-Host "       .\scripts\Push-ChocolateyPackage.ps1 -ApiKey '<your-key>'" -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Cleanup staging
# ---------------------------------------------------------------------------
Remove-Item -Recurse -Force $stagingDir

Write-Host "`nDone." -ForegroundColor Green