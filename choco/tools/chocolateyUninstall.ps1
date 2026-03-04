$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  softwareName   = 'qbPortWeaver*'
  fileType       = 'msi'
  # MSI silent uninstall flags: /qn = no UI, /norestart = suppress reboot prompt
  silentArgs     = '/qn /norestart'
  validExitCodes = @(0, 3010, 1641)
}

[array]$key = Get-UninstallRegistryKey -SoftwareName $packageArgs['softwareName']

if ($key.Count -eq 1) {
  $key | ForEach-Object {
    # For MSI packages the registry key name IS the ProductCode GUID
    $packageArgs['file'] = $_.PSChildName
    Uninstall-ChocolateyPackage @packageArgs
  }
} elseif ($key.Count -eq 0) {
  Write-Warning "$($packageArgs['packageName']) has already been uninstalled by other means."
} else {
  Write-Warning "$($key.Count) matching uninstall entries found - skipping to avoid accidental removal."
  Write-Warning "Matches: $(($key | Select-Object -ExpandProperty DisplayName) -join ', ')"
}

# Optionally remove user data (disabled by default - uncomment if desired):
# $appDataPath = Join-Path $env:LOCALAPPDATA 'qbPortWeaver'
# if (Test-Path $appDataPath) {
#   Remove-Item -Recurse -Force $appDataPath
#   Write-Host "Removed user data at $appDataPath"
# }
