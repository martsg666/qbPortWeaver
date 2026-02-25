$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  softwareName   = 'qbPortWeaver*'
  fileType       = 'exe'
  # NSIS silent uninstall flag
  silentArgs     = '/S'
  validExitCodes = @(0)
}

[array]$key = Get-UninstallRegistryKey -SoftwareName $packageArgs['softwareName']

if ($key.Count -eq 1) {
  $key | ForEach-Object {
    $packageArgs['file'] = $_.UninstallString
    Uninstall-ChocolateyPackage @packageArgs
  }
} elseif ($key.Count -eq 0) {
  Write-Warning "$($packageArgs['packageName']) has already been uninstalled by other means."
} else {
  Write-Warning "$($key.Count) matching uninstall entries found — skipping to avoid accidental removal."
  Write-Warning "Matches: $(($key | Select-Object -ExpandProperty DisplayName) -join ', ')"
}

# Optionally remove user data (disabled by default — uncomment if desired):
# $appDataPath = Join-Path $env:LOCALAPPDATA 'qbPortWeaver'
# if (Test-Path $appDataPath) {
#   Remove-Item -Recurse -Force $appDataPath
#   Write-Host "Removed user data at $appDataPath"
# }
