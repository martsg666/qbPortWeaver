using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace qbPortWeaver.Shared;

/// <summary>Outcome of <see cref="ProcessKillHelper.KillProcessTreeWithEscalation"/>.</summary>
public enum ProcessKillOutcome
{
    /// <summary>Process had already exited before the kill attempt (InvalidOperationException).</summary>
    AlreadyExited,
    /// <summary>Stage 1 (<see cref="Process.Kill(bool)"/>) succeeded and the process exited within the timeout.</summary>
    KilledByProcessKill,
    /// <summary>Stage 2 (<c>taskkill /F /T</c>) succeeded and the process exited within the timeout.</summary>
    KilledByTaskkill,
    /// <summary>Stage 3 (retry of <see cref="Process.Kill(bool)"/>) succeeded and the process exited within the timeout.</summary>
    KilledByProcessKillRetry,
    /// <summary>Process is access-denied or protected (Win32Exception) - no further escalation will help.</summary>
    AccessDenied,
    /// <summary>All three escalation stages completed without the process exiting within their timeouts.</summary>
    StillRunning,
}

/// <summary>Result of a process-tree kill, including the outcome and the taskkill-launch error if any.</summary>
/// <param name="Outcome">Which stage exited the process, or the failure mode.</param>
/// <param name="TaskkillError">Set when the stage-2 taskkill subprocess failed to launch. Callers may log this at Warn.</param>
public sealed record ProcessKillResult(ProcessKillOutcome Outcome, Exception? TaskkillError);

/// <summary>
/// Three-stage process-tree kill escalation shared by <c>AppConstants.KillProcess</c> (main app)
/// and <c>AutoRecovery.KillServiceProcess</c> (helper service). Centralising the escalation
/// prevents the two implementations from drifting; each consumer logs the outcome according to
/// its own context (PID-aware for the helper, simpler for the main app).
/// </summary>
public static class ProcessKillHelper
{
    // Absolute path to taskkill.exe, cached once at type-init. Stage-2 fallback when Process.Kill alone
    // does not bring the target down within the timeout.
    private static readonly string SystemTaskkillPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");

    /// <summary>
    /// Escalates kill attempts on <paramref name="process"/>:
    /// <list type="number">
    /// <item><see cref="Process.Kill(bool)"/> with full tree, then wait <paramref name="timeoutMs"/>.</item>
    /// <item><c>taskkill /F /T /PID &lt;n&gt;</c>, then wait <paramref name="timeoutMs"/>.</item>
    /// <item>Retry <see cref="Process.Kill(bool)"/>, then wait <paramref name="timeoutMs"/>.</item>
    /// </list>
    /// Returns as soon as the process exits, or after all three stages have been tried. The
    /// caller is responsible for disposing <paramref name="process"/>.
    /// </summary>
    public static ProcessKillResult KillProcessTreeWithEscalation(Process process, int timeoutMs)
    {
        // Stage 1: Process.Kill
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return new ProcessKillResult(ProcessKillOutcome.AlreadyExited, null);
        }
        catch (Win32Exception)
        {
            return new ProcessKillResult(ProcessKillOutcome.AccessDenied, null);
        }
        if (process.WaitForExit(timeoutMs))
            return new ProcessKillResult(ProcessKillOutcome.KilledByProcessKill, null);

        // Stage 2: taskkill /F /T - run as a child process and wait
        Exception? taskkillError = null;
        try
        {
            using var taskkill = Process.Start(ProcessHelpers.CreateHiddenStartInfo(
                SystemTaskkillPath, $"/F /T /PID {process.Id}"));
            taskkill?.WaitForExit(timeoutMs);
        }
        catch (Exception ex)
        {
            taskkillError = ex;
        }
        if (process.WaitForExit(timeoutMs))
            return new ProcessKillResult(ProcessKillOutcome.KilledByTaskkill, taskkillError);

        // Stage 3: retry Process.Kill - taskkill may have weakened the tree even if it did not exit
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return new ProcessKillResult(ProcessKillOutcome.AlreadyExited, taskkillError);
        }
        catch (Win32Exception)
        {
            return new ProcessKillResult(ProcessKillOutcome.AccessDenied, taskkillError);
        }
        return process.WaitForExit(timeoutMs)
            ? new ProcessKillResult(ProcessKillOutcome.KilledByProcessKillRetry, taskkillError)
            : new ProcessKillResult(ProcessKillOutcome.StillRunning, taskkillError);
    }
}
