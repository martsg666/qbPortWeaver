namespace qbPortWeaver;

/// <summary>The application's own files on disk: where they live, and how to read and write them safely.
/// <para>Reads share the file so a concurrent atomic rewrite is never blocked, and writes go through a
/// temp file renamed over the target, so a reader sees either the whole old file or the whole new one.</para></summary>
public static class AppFiles
{
    private const string StatusFileName = "qbPortWeaver.status.json";

    // PublicationOnly, not the default ExecutionAndPublication. The default caches a thrown exception
    // permanently, so one transient failure here - a redirected %LocalAppData% on a share that is
    // briefly unreachable, a profile hive still mounting at logon - would leave the log file, status
    // file, port history and TMDB cache dead for the whole process lifetime, and dead in a way that
    // cannot be reported, since the log is one of the things it takes down. PublicationOnly re-runs the
    // factory on the next access instead.
    //
    // Safe here only because the factory is idempotent: CreateDirectory on an existing directory is a
    // no-op returning the same path, so two threads racing costs one redundant syscall and both publish
    // the same string. Note the three Lazy fields in NicotinePluginInstaller deliberately keep the
    // default and document why - their factories read already-loaded assembly resources and cannot fail
    // transiently. That reasoning does not transfer to a factory that touches the file system.
    private static readonly Lazy<string> _appDataFolder = new(() => Directory.CreateDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppIdentity.AppName)
    ).FullName, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>The application's own data folder under <c>%LocalAppData%</c>, created on first access.</summary>
    /// <remarks>Also where the Nicotine+ bridge plugin publishes its connection details, since it is
    /// a fixed path both sides can derive without knowing how the other was installed.</remarks>
    internal static string AppDataFolder => _appDataFolder.Value;

    /// <summary>Returns the full path to the application log file.</summary>
    public static string GetLogFilePath() => Path.Combine(AppDataFolder, AppIdentity.LogFileName);

    /// <summary>Returns the full path to the application status JSON file.</summary>
    public static string GetStatusFilePath() => Path.Combine(AppDataFolder, StatusFileName);

    /// <summary>Returns the full path for a named data file stored in the application data folder.</summary>
    internal static string GetDataFilePath(string fileName) => Path.Combine(AppDataFolder, fileName);

    /// <summary>Deletes a file if it exists, swallowing IO and permission errors. Never throws.</summary>
    internal static void DeleteFileSafely(string path)
    {
        try
        {
            File.Delete(path); // no-op if the file does not exist
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Guarded with IsInitialized because this is a generic file helper reachable from
            // early startup paths before LogManager.Initialize runs. The kill/service helpers
            // below call LogManager.Instance unguarded by design - they only run inside the
            // active sync loop, which cannot start until after initialization.
            if (LogManager.IsInitialized)
                LogManager.Instance.LogDebug($"AppFiles.DeleteFileSafely: Could not delete '{path}': {ex.Message}");
        }
    }

    /// <summary>Reads a text file in a way that does not block a concurrent atomic rewrite of it.</summary>
    /// <remarks>
    /// <see cref="File.ReadAllText(string)"/> opens with <see cref="FileShare.Read"/>, which withholds
    /// the DELETE access Windows requires to rename a file over one that is open - so a read in flight
    /// makes <see cref="WriteAtomic(string, string)"/>'s final rename fail, in the <em>writer</em>.
    /// Granting <see cref="FileShare.Delete"/> lets the two overlap. Nothing is torn by that: the
    /// rename is atomic, so a reader still sees either the whole old file or the whole new one.
    /// Failure modes are otherwise <see cref="File.ReadAllText(string)"/>'s, so callers keep their
    /// own error handling.
    /// </remarks>
    internal static string ReadAllTextShared(string path)
    {
        using var stream = OpenShared(path);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Reads a text file's lines without blocking a concurrent atomic rewrite - see
    /// <see cref="ReadAllTextShared"/> for why.</summary>
    internal static string[] ReadAllLinesShared(string path)
    {
        using var stream = OpenShared(path);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return [.. lines];
    }

    // FileShare.ReadWrite so a writer appending to the file (the log) is not blocked either, and
    // FileShare.Delete so an atomic rename over the target can complete while this handle is open.
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Writes text to a temp file then atomically renames it over the target.
    /// If the process is killed mid-write, only the temp file is lost and the original is untouched.</summary>
    internal static void WriteAtomic(string path, string content) =>
        WriteAtomicCore(path, temp => File.WriteAllText(temp, content));

    /// <summary>Writes lines to a temp file then atomically renames it over the target.</summary>
    /// <remarks>UTF-8 with no byte-order mark, matching the single-string overload
    /// (<see cref="File.WriteAllText(string, string)"/> defaults to the same). Written explicitly
    /// because Nicotine+ parses a BOM as part of the first key name in its config file.</remarks>
    internal static void WriteAtomic(string path, string[] lines) =>
        WriteAtomicCore(path, temp => File.WriteAllLines(temp, lines, Utf8NoBom));

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Distinctive enough that a sweep can recognise our leftovers without touching anything else in
    // the folder - which matters because one caller writes into Nicotine+'s config folder.
    private const string TempFilePrefix = ".qbpw-";
    private const string TempFileSuffix = ".tmp";

    /// <summary>Deletes temp files an interrupted atomic write left behind in <paramref name="folder"/>.</summary>
    /// <remarks>
    /// <see cref="WriteAtomicCore"/> deletes its own temp file when the write throws, but nothing can
    /// run at a process kill or power loss between writing the temp file and renaming it. Because each
    /// write picks a fresh name, no later write reclaims that file the way a fixed
    /// <c>&lt;target&gt;.tmp</c> would, so without this they accumulate for the life of the install.
    /// <para>Safe to run only where no write is in flight: callers do so at startup, before the sync
    /// loop begins, and the single-instance mutex rules out another instance owning one.</para>
    /// </remarks>
    internal static void SweepOrphanedTempFiles(string folder)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, $"{TempFilePrefix}*{TempFileSuffix}"))
                DeleteFileSafely(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            if (LogManager.IsInitialized)
                LogManager.Instance.LogDebug($"AppFiles.SweepOrphanedTempFiles: '{folder}' - {ex.Message}");
        }
    }

    // Shared write-then-rename core. The temp file is a uniquely named sibling of the target rather
    // than "<target>.tmp" so two writers to the same path cannot fight over one temp file - which
    // no current caller does, the sync cycle being serialised, but the guarantee costs nothing.
    // The cost is that an interrupted write leaves a name nothing reuses; SweepOrphanedTempFiles
    // clears those. Same folder is required: File.Move is only atomic within a volume.
    private static void WriteAtomicCore(string path, Action<string> writeTo)
    {
        string folder = Path.GetDirectoryName(path) ?? string.Empty;
        if (folder.Length > 0) Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, $"{TempFilePrefix}{Guid.NewGuid():N}{TempFileSuffix}");
        try
        {
            writeTo(temp);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            DeleteFileSafely(temp);
            throw;
        }
    }

    /// <summary>Returns the full path to the ProtonVPN log file, resolved from the registry setting,
    /// or an empty string when that setting is blank.</summary>
    /// <remarks>Empty rather than the bare folder, because <see cref="Path.Combine(string, string)"/>
    /// returns the first argument when the second is empty. Handing the caller
    /// <c>%LocalAppData%</c> made <c>ProtonVpnManager</c>'s own empty-path guard unreachable - a blank
    /// setting fell through to the next check and was reported as "log file does not exist:
    /// %LocalAppData%", which sends the user looking for a missing file instead of at the setting.</remarks>
    public static string GetProtonVpnLogFilePath()
    {
        string configured = RegistrySettingsManager.GetAppValue(RegistrySettingsManager.KeyProtonVpnLogFilePath);
        return configured.Length == 0
            ? string.Empty
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), configured);
    }
}
