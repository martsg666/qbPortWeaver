Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  fileType       = 'msi'
  url64bit       = 'TEMPLATE_URL'
  checksum64     = 'TEMPLATE_CHECKSUM'
  checksumType64 = 'sha256'
  # MSI silent install flags: /qn = no UI, /norestart = suppress reboot prompt
  silentArgs     = '/qn /norestart'
  validExitCodes = @(0, 3010, 1641)
}

Install-ChocolateyPackage @packageArgs
