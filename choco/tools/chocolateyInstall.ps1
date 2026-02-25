$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  fileType       = 'exe'
  url64bit       = 'TEMPLATE_URL'
  checksum64     = 'TEMPLATE_CHECKSUM'
  checksumType64 = 'sha256'
  # NSIS silent install flag
  silentArgs     = '/S'
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
