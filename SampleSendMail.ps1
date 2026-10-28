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
    # Same order as the app. The offline hold comes first because the cap does not apply while the
    # machine is offline - it keeps retrying on a backoff. The cap comes next, ahead of the sustained
    # hold: that hold defers the next attempt, the cap means none is coming until a port reads
    # successfully. A missing key is an older app version, where the cap did not exist, so absent is
    # correctly falsy here. Both holds are absolute instants, not remaining durations.
    if ($null -ne $s.recoveryHoldUntil) { return "Holding - cannot confirm internet, retry at $($s.recoveryHoldUntil)" }
    if ($s.recoverySuspended) { return 'Suspended - restarts did not help, resumes when a port is found' }
    if ($null -ne $s.recoverySustainedUntil) { return "Holding - failures too recent, retry at $($s.recoverySustainedUntil)" }
    if ($s.recoveryFailedCycles -ge $s.recoveryTriggerCycles) { return 'Will trigger on the next failed cycle' }
    if ($s.recoveryFailedCycles -gt 0) { return "$($s.recoveryFailedCycles) of $($s.recoveryTriggerCycles) failed cycles" }
    return $null
}

# What the port-closed trigger is doing, or $null when it is idle.
function PortClosedState($s) {
    # While traffic leaves from a different address than the port is forwarded on, the app reconnects
    # the VPN for a new exit address, up to vpnExitReconnectsMax times, then holds and asks for a
    # different server. The closed checks the mismatch explains are not counted.
    if ($s.vpnExitAddressMismatch) {
        # Behind another NAT no reconnect is made: none can change the address.
        # Short, as on the Status panel; the addresses and the advice are in the Address Match row.
        if (BehindAnotherNat $s.vpnForwardAddress) { return 'Held - the gateway is behind another NAT' }
        if (-not $s.vpnExitReconnectGaveUp) { return "Reconnecting for a matching exit address ($($s.vpnExitReconnects) of $($s.vpnExitReconnectsMax))" }
        return "Held - no matching exit address after $($s.vpnExitReconnectsMax) attempts"
    }
    # One-shot: once it fires it stays disarmed until a scheduled check reports the port open.
    if (-not $s.portClosedRecoveryArmed) { return 'Triggered - waiting for the next scheduled check' }
    # The count has reached the threshold but a time hold keeps the trigger back: the port has not
    # stayed closed long enough yet, or an exit-address reconnect is still settling. An absolute instant.
    if ($null -ne $s.portClosedRecoveryHeldUntil) { return "Holding - port closed too recently, retry at $($s.portClosedRecoveryHeldUntil)" }
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

# Whether traffic leaves from the address the port is forwarded on. The app looks this up
# only when a port check reports the port closed or cannot determine it, and only for NAT-PMP and ProtonVPN, so
# "Not checked" is normal while the port is open, for other providers, and for app versions without these keys.
function AddressMatch($s) {
    if ($s.vpnExitAddressMismatch) {
        # A private or carrier-grade NAT forward address is a home router behind a second NAT, where
        # switching VPN servers is not the fix - the same split the app makes.
        if (BehindAnotherNat $s.vpnForwardAddress) { return 'No - the gateway is itself behind another NAT, so the port cannot be reached from the Internet' }
        # Mismatches were seen on ProtonVPN's OpenVPN tunnel and not on Proton WireGuard on the same server, hence the protocol advice there.
        # After the give-up the advice drops the reconnect, as the Status panel's tooltip does.
        $prefix = "No - traffic leaves from $($s.vpnExitAddress), not from $($s.vpnForwardAddress) where the port is forwarded; "
        if ($s.vpnExitReconnectGaveUp) {
            if ($s.vpnProtonOverOpenVpn) { return $prefix + 'switch ProtonVPN to Proton WireGuard (UDP), or connect to a different server' }
            return $prefix + 'connect the VPN to a different server'
        }
        if ($s.vpnProtonOverOpenVpn) { return $prefix + "ProtonVPN's OpenVPN connections often do this, switch ProtonVPN to Proton WireGuard (UDP), or connect to a different server" }
        return $prefix + 'reconnecting may give a different exit address, or connect to a different server'
    }
    if ($s.vpnForwardAddress -and $s.vpnExitAddress) { return 'Yes' }
    return 'Not checked'
}
function OrNotChecked($val) { if ($val) { $val } else { 'Not checked' } }
function BehindAnotherNat($ip) {
    $parsed = $null
    if (-not [System.Net.IPAddress]::TryParse([string]$ip, [ref]$parsed)) { return $false }
    $b = $parsed.GetAddressBytes()
    if ($b.Length -ne 4) { return $false }
    return ($b[0] -eq 10) -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or ($b[0] -eq 192 -and $b[1] -eq 168) -or
           ($b[0] -eq 100 -and $b[1] -ge 64 -and $b[1] -le 127) -or ($b[0] -eq 169 -and $b[1] -eq 254)
}

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
$(Row 'Forward Address' (OrNotChecked $status.vpnForwardAddress))
$(Row 'Exit Address'    (OrNotChecked $status.vpnExitAddress))
$(Row 'Address Match'   (AddressMatch $status))
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
