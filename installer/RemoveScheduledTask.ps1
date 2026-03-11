# Removes the qbPortWeaver VPN auto-recovery scheduled task.
# Run automatically by the MSI uninstaller; can also be run manually to remove the task.
Unregister-ScheduledTask -TaskName 'qbPortWeaver-Vpn-AutoRecovery' -Confirm:$false -ErrorAction SilentlyContinue
