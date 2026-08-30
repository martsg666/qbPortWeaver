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

# What the failed-cycle trigger is doing, or $null when it is idle. Its own function so the caller
# can fall through to the port-closed trigger, mirroring how the app itself splits the two.
function FailedCycleState($s) {
    # Checked ahead of the holds, as the app does: they defer the next attempt, this one means none is
    # coming until a port reads successfully. A missing key is an older app version, where the cap did
    # not exist, so absent is correctly falsy here.
    # Both holds are absolute instants, not remaining durations. The offline hold is checked first
    # because the cap does not apply while the machine is offline - it keeps retrying on a backoff.
    if ($null -ne $s.recoveryHoldUntil) { return "Holding - no internet connection, retry at $($s.recoveryHoldUntil)" }
    if ($s.recoverySuspended) { return 'Suspended - restarts did not restore the port, resumes when one is found' }
    if ($null -ne $s.recoverySustainedUntil) { return "Holding - failures too recent, retry at $($s.recoverySustainedUntil)" }
    if ($s.recoveryFailedCycles -ge $s.recoveryTriggerCycles) { return 'Will trigger on the next failed cycle' }
    if ($s.recoveryFailedCycles -gt 0) { return "$($s.recoveryFailedCycles) of $($s.recoveryTriggerCycles) failed cycles" }
    return $null
}

# What the port-closed trigger is doing, or $null when it is idle.
function PortClosedState($s) {
    # One-shot: once it fires it stays disarmed until a scheduled check reports the port open.
    if (-not $s.portClosedRecoveryArmed) { return 'Triggered - waiting for the next scheduled check' }
    if ($s.portClosedRecoveryChecks -gt 0) { return "$($s.portClosedRecoveryChecks) of $($s.portClosedRecoveryTriggerChecks) closed checks" }
    return $null
}

# Mirrors the Status panel's Auto-recovery line. Auto-recovery is two independent triggers with two
# independent settings - consecutive failed cycles, and a port confirmed closed from outside - and
# either one can restart the VPN with the other switched off, so "Disabled" needs both to be off.
# The failed-cycle states are reported first and fall through to the port-closed ones.
function RecoveryState($s) {
    # A threshold of 0 (or a missing key) means no cycle has published one: a status file written by
    # an app version from before these keys existed, or a cycle that failed before reading config.
    if ($null -eq $s.recoveryTriggerCycles -or $s.recoveryTriggerCycles -eq 0) { return 'Not reported by this app version' }
    if (-not $s.recoveryEnabled -and -not $s.portClosedRecoveryEnabled) { return 'Disabled' }

    if ($s.recoveryEnabled) {
        $failedCycle = FailedCycleState $s
        if ($null -ne $failedCycle) { return $failedCycle }
    }

    if ($s.portClosedRecoveryEnabled) {
        $portClosed = PortClosedState $s
        if ($null -ne $portClosed) { return $portClosed }
    }

    return 'Idle'
}

function OnOff($enabled) { if ($enabled) { 'Enabled' } else { 'Disabled' } }

$Body = @"
<table style='font-family:monospace;font-size:13px;border-collapse:collapse;'>
$(Section '[App]')
$(Row 'Version'         $status.appVersion)
$(Row 'Timestamp'       $status.timestamp)
$(Row 'Update Interval' "$($status.updateIntervalSeconds)s")
$(Row 'Next Sync'       $(if ($status.nextSyncAt) { $status.nextSyncAt } else { 'Not published by this app version' }))
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
$(Section '[Recovery]')
$(Row 'State'                (RecoveryState $status))
$(Row 'Failed-cycle trigger' (OnOff $status.recoveryEnabled))
$(Row 'Failed cycles'        "$($status.recoveryFailedCycles) of $($status.recoveryTriggerCycles)")
$(Row 'Port-closed trigger'  (OnOff $status.portClosedRecoveryEnabled))
$(Row 'Closed checks'        "$($status.portClosedRecoveryChecks) of $($status.portClosedRecoveryTriggerChecks)")
$(Row 'Port-closed armed'    $(if ($status.portClosedRecoveryArmed) { 'Yes' } else { 'No - already ran, waiting for the next scheduled check' }))
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
