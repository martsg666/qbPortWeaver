namespace qbPortWeaver.Shared
{
    /// <summary>
    /// App-wide identity constants shared by the main app and the helper service.
    /// Both processes derive their AppData folder, log file path, and HKCU subkey from these,
    /// so the values must agree.
    /// </summary>
    public static class AppIdentity
    {
        /// <summary>Application name used as the AppData subfolder, EventLog source, and registry root.</summary>
        public const string AppName = "qbPortWeaver";

        /// <summary>HKCU subkey holding app-level settings (above the per-section keys).</summary>
        public const string AppRegistryKey = @"Software\" + AppName;

        /// <summary>HKCU value name (under <see cref="AppRegistryKey"/>) holding the pipe session token.</summary>
        public const string PipeSessionTokenKey = "pipeSessionToken";

        /// <summary>Log file name in <c>%LocalAppData%\qbPortWeaver\</c>. Written by both the main app and the helper service.</summary>
        public const string LogFileName = "qbPortWeaver.log";
    }
}
