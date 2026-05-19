namespace qbPortWeaver.Shared;

/// <summary>
/// Wire-protocol constants for the named pipe between the user-session tray app
/// (qbPortWeaver.exe) and the SYSTEM helper service (qbPortWeaver.HelperService.exe).
/// Both processes reference these constants directly, so the protocol cannot drift.
/// </summary>
public static class HelperProtocol
{
    /// <summary>Named pipe used by the tray app to send action requests to the helper service.</summary>
    public const string PipeName = "qbPortWeaverHelper";

    /// <summary>Restart action: target is a Windows service name; helper stops and starts it.</summary>
    public const string ActionRestart = "restart";

    /// <summary>Cycle-adapter action: target is a network adapter name; helper disables and re-enables it via netsh.</summary>
    public const string ActionCycleAdapter = "cycle-adapter";

    /// <summary>Result key: number of WARN-level entries the helper wrote during the action.</summary>
    public const string ResultWarnKey = "warn";

    /// <summary>Result key: number of ERROR-level entries the helper wrote during the action.</summary>
    public const string ResultErrorKey = "error";

    /// <summary>Result sentinel: the helper rejected the request (session token mismatch).</summary>
    public const string ResultRejectedSentinel = "rejected";
}
