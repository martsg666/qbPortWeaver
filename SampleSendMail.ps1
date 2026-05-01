# Read status file
$status = Get-Content "$env:LOCALAPPDATA\qbPortWeaver\qbPortWeaver.status.json" | ConvertFrom-Json

# Email configuration
$SmtpServer = "smtp.gmail.com"
$SmtpPort = 587
$From = "FROM_EMAIL@gmail.com"
$To = "TO_EMAIL@gmail.com"
$Subject = "qbPortWeaver - Port Changed to $($status.clientPort)"
$Body = @"
App Version:         $($status.appVersion)
Timestamp:           $($status.timestamp)
Update Interval:     $($status.updateIntervalSeconds)s

VPN Provider:        $($status.vpnProvider)
VPN Connected:       $($status.vpnConnected)
VPN Port:            $($status.vpnPort)

Client:              $($status.client)
Client Running:      $($status.clientRunning)
Client Port:         $($status.clientPort)
Client Prev Port:    $($status.clientPreviousPort)
Port Changed:        $($status.portChanged)

Status:              $($status.status)
Message:             $($status.message)
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