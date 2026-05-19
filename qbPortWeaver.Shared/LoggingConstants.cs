namespace qbPortWeaver.Shared;

/// <summary>
/// Log-file formatting constants and helpers shared by <c>LogManager</c> (main app) and
/// <c>HelperLogger</c> (helper service). Both processes write to the same on-disk log file,
/// so the entry format, column widths, and level labels must match exactly for the log
/// viewer to render entries consistently.
/// </summary>
public static class LoggingConstants
{
    /// <summary>Subsystem label used by the helper service when writing log entries.</summary>
    public const string HelperServiceSubsystem = "HelperService";

    /// <summary>Width of the subsystem column in log entries. Pads every label to this width.</summary>
    public const int SubsystemMaxLength = 13; // "HelperService".Length

    /// <summary>Timestamp format used in every log entry. Sortable, fixed-width, no timezone marker.</summary>
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    // Pre-padded level labels for log entry alignment. Every label is exactly 5 chars wide
    // so the level column has a fixed width; INFO and WARN have a trailing space, ERROR and
    // DEBUG fill the width naturally.
    public const string LevelInfoLabel = "INFO ";
    public const string LevelWarnLabel = "WARN ";
    public const string LevelErrorLabel = "ERROR";
    public const string LevelDebugLabel = "DEBUG";

    /// <summary>
    /// Formats a single log entry with the standard layout used by both processes:
    /// <c>"yyyy-MM-dd HH:mm:ss | LEVEL | Subsystem     | message\n"</c>. Used by
    /// <c>LogManager.FormatEntry</c> (main app) and <c>HelperLogger.WriteLog</c>
    /// (helper service) so the on-disk format cannot drift.
    /// </summary>
    public static string FormatLogEntry(System.DateTime timestamp, string levelLabel, string subsystem, string message) =>
        $"{timestamp.ToString(DateFormat)} | {levelLabel} | {subsystem.PadRight(SubsystemMaxLength)} | {message}{System.Environment.NewLine}";
}
