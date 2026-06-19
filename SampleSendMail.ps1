# Read status file
$status = Get-Content "$env:LOCALAPPDATA\qbPortWeaver\qbPortWeaver.status.json" | ConvertFrom-Json

# Email configuration
$SmtpServer = "smtp.gmail.com"
$SmtpPort = 587
$From = "FROM_EMAIL@gmail.com"
$To = "TO_EMAIL@gmail.com"
$Subject = "qbPortWeaver - Port Changed to $($status.clientPort)"
function Row($key, $val) { "<tr><td style='padding:1px 16px 1px 0;color:#888;'>$key</td><td>$val</td></tr>" }
function Section($title) { "<tr><td colspan='2' style='padding:10px 0 2px;font-weight:bold;'>$title</td></tr>" }

$Body = @"
<table style='font-family:monospace;font-size:13px;border-collapse:collapse;'>
$(Section '[App]')
$(Row 'Version'         $status.appVersion)
$(Row 'Timestamp'       $status.timestamp)
$(Row 'Update Interval' "$($status.updateIntervalSeconds)s")
$(Section '[VPN]')
$(Row 'Provider'  $status.vpnProvider)
$(Row 'Connected' $status.vpnConnected)
$(Row 'Port'      $status.vpnPort)
$(Section '[Client]')
$(Row 'Name'          $status.client)
$(Row 'Running'       $status.clientRunning)
$(Row 'Port'          $status.clientPort)
$(Row 'Previous Port' $status.clientPreviousPort)
$(Row 'Port Changed'  $status.portChanged)
$(Row 'Port Verified' $(if ($null -eq $status.portVerified) { 'Not tested this cycle' } else { $status.portVerified }))
$(Section '[Result]')
$(Row 'Status'  ($status.status.Substring(0,1).ToUpper() + $status.status.Substring(1)))
$(Row 'Message' $status.message)
</table>
"@

# Credentials
$Username = "USERNAME@gmail.com"
$Password = ConvertTo-SecureString "GMAIL_APP_PASSWORD" -AsPlainText -Force
$Credential = New-Object System.Management.Automation.PSCredential($Username, $Password)

# Send the email
Send-MailMessage -From $From `
                 -To $To `
                 -Subject $Subject `
                 -Body $Body `
                 -BodyAsHtml `
                 -SmtpServer $SmtpServer `
                 -Port $SmtpPort `
                 -UseSsl `
                 -Credential $Credential