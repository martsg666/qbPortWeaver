# Creates the qbPortWeaver VPN auto-recovery scheduled task.
# Run automatically by the MSI installer; can also be run manually to recreate the task.
# $UserSid is the installing user's SID passed by MSI via [UserSID] — used to resolve
# the user's LocalAppData path so the task argument is correct when running as SYSTEM.
param([string]$UserSid = '')
try {
    if ($UserSid) {
        $profilePath  = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$UserSid" -ErrorAction Stop).ProfileImagePath
        $localAppData = Join-Path $profilePath 'AppData\Local'
    } else {
        $localAppData = $env:LOCALAPPDATA
    }

    # Register event source so the app (running as a non-admin user) can write to the
    # Application log to trigger this task. Source registration requires HKLM write access,
    # so it must be done here (running as SYSTEM) rather than by the app at runtime.
    if (-not [System.Diagnostics.EventLog]::SourceExists('qbPortWeaver')) {
        [System.Diagnostics.EventLog]::CreateEventSource('qbPortWeaver', 'Application')
    }

    # XML-escape the exe path and arguments for safe embedding in the task XML.
    # $(serviceName) is a Task Scheduler ValueQueries variable — it is NOT a PowerShell variable
    # (the backtick escapes the $ so PowerShell emits a literal dollar sign).
    $exeXml = [System.Security.SecurityElement]::Escape("$PSScriptRoot\qbPortWeaver.exe")
    $argXml = [System.Security.SecurityElement]::Escape("--vpn-autorecovery `"`$(serviceName)`" `"$localAppData`"")

    # New-ScheduledTaskTrigger does not support event triggers; use the XML task definition.
    # The EventTrigger fires when qbPortWeaver writes EventID 1001 to the Application log.
    # Subscription inner XML must be XML-escaped as it is embedded inside the outer task XML.
    $taskXml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>qbPortWeaver VPN auto-recovery — restarts the VPN service when the VPN is not connected.</Description>
  </RegistrationInfo>
  <Triggers>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="Application"&gt;&lt;Select Path="Application"&gt;*[System[Provider[@Name='qbPortWeaver'] and EventID=1001]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
      <ValueQueries>
        <Value name="serviceName">Event/EventData/Data</Value>
      </ValueQueries>
    </EventTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$exeXml</Command>
      <Arguments>$argXml</Arguments>
    </Exec>
  </Actions>
</Task>
"@

    Register-ScheduledTask -Force -TaskName 'qbPortWeaver-Vpn-AutoRecovery' -Xml $taskXml -ErrorAction Stop
} catch {
    exit 1
}
