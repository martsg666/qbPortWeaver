namespace qbPortWeaver.Shared
{
    /// <summary>
    /// Log-file formatting constants shared by <c>LogManager</c> (main app) and <c>HelperLogger</c>
    /// (helper service). Both write to the same on-disk log file, so column widths and subsystem
    /// names must match for the log viewer to render entries consistently.
    /// </summary>
    public static class LoggingConstants
    {
        /// <summary>Subsystem label used by the helper service when writing log entries.</summary>
        public const string HelperServiceSubsystem = "HelperService";

        /// <summary>Width of the subsystem column in log entries. Pads every label to this width.</summary>
        public const int SubsystemMaxLength = 13; // "HelperService".Length
    }
}
