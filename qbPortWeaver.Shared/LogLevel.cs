namespace qbPortWeaver.Shared;

/// <summary>Severity level for log entries. Shared by LogManager (main app) and HelperLogger (helper service) so both write the same level labels.</summary>
public enum LogLevel { Info, Warn, Error, Debug }
