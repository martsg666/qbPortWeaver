using System.Diagnostics;

namespace qbPortWeaver;

/// <summary>Terminates client and VPN processes, delegating the escalation itself to
/// <see cref="ProcessKillHelper"/> so the tray app and the helper service share one implementation.</summary>
public static class ProcessControl
{
    /// <summary>
    /// Kills a process (including its entire process tree) and waits up to <paramref name="timeoutMs"/> for exit.
    /// Escalation lives in <see cref="ProcessKillHelper.KillProcessTreeWithEscalation"/> so the helper
    /// service can use the same logic. This wrapper logs the per-stage outcome at Warn using
    /// <paramref name="contextLabel"/> as the prefix so the user sees what kind of process failed.
    /// Returns <see langword="true"/> if the process exited (or had already exited), <see langword="false"/> if it could not be killed.
    /// The caller is responsible for disposing <paramref name="process"/>.
    /// </summary>
    public static bool KillProcess(Process process, string contextLabel, int timeoutMs = 5000)
    {
        var result = ProcessKillHelper.KillProcessTreeWithEscalation(process, timeoutMs);
        if (result.TaskkillError is not null)
            LogManager.Instance.LogMessage($"Failed to run taskkill fallback for {contextLabel} (PID {process.Id}): {result.TaskkillError.Message}", LogLevel.Warn);

        switch (result.Outcome)
        {
            case ProcessKillOutcome.AccessDenied:
                LogManager.Instance.LogMessage($"{contextLabel} (PID {process.Id}) could not be killed - access denied or process protected", LogLevel.Warn);
                return false;
            case ProcessKillOutcome.StillRunning:
                LogManager.Instance.LogMessage($"{contextLabel} (PID {process.Id}) still running after kill attempts", LogLevel.Warn);
                return false;
            default:
                return true;
        }
    }

    /// <summary>Kills all running processes matching <paramref name="processName"/>; per-process outcome is logged inside <see cref="KillProcess"/>.</summary>
    internal static void KillProcessesByName(string processName, int killTimeoutMs, string clientName)
    {
        foreach (var proc in Process.GetProcessesByName(processName))
        {
            try
            {
                KillProcess(proc, $"{clientName} process", killTimeoutMs);
            }
            catch (Exception ex) { LogManager.Instance.LogDebug($"ProcessControl.KillProcessesByName: Failed to kill a {clientName} process: {ex.Message}"); }
            finally { proc.Dispose(); }
        }
    }
}
