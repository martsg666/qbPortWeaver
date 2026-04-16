# Read status file
$status = Get-Content "$env:LOCALAPPDATA\qbPortWeaver\qbPortWeaver.status.json" | ConvertFrom-Json

# Email configuration
$SmtpServer = "smtp.gmail.com"
$SmtpPort = 587
$From = "FROM_EMAIL@gmail.com"
$To = "TO_EMAIL@gmail.com"
$Subject = "qbPortWeaver - Port Changed to $($status.qBittorrentPort)"
$Body = @"
VPN Provider: $($status.vpnProvider)
VPN Connected: $($status.vpnConnected)
VPN Port: $($status.vpnPort)
qBittorrent Previous Port: $($status.qBittorrentPreviousPort)
qBittorrent Port: $($status.qBittorrentPort)
Port Changed: $($status.portChanged)
Status: $($status.status)
Message: $($status.message)
Last Run: $($status.timestamp)
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
                 -SmtpServer $SmtpServer `
                 -Port $SmtpPort `
                 -UseSsl `
                 -Credential $Credential