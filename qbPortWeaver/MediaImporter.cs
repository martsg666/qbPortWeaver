using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace qbPortWeaver;

/// <summary>File-level infrastructure for the media import pipeline: file transfer (hardlink/copy/move), library fingerprint index, source and library cache management, and file utilities.</summary>
internal static partial class MediaImporter
{
    // Library index: fingerprints (size + partial SHA-256) of every file in the library folders.
    private const int FullRebuildIntervalCycles = 10; // force a full library index rebuild every N cycles to catch external changes (e.g. deletions in Plex)
    // Maximum concurrent fingerprint reads for both source and library fingerprinting.
    // I/O-bound: storage throughput is the constraint, not CPU count.
    // 8 concurrent reads balances throughput vs not overwhelming slower storage.
    internal const int FingerprintParallelism = 8;
    private static HashSet<string>? _libraryFingerprints;
    private static readonly object _libraryLock = new();
    private static readonly SemaphoreSlim _libraryBuildSemaphore = new(1, 1);
    private static int _libraryBuildCycleCount;
    private static string _lastMoviesLibraryPath = string.Empty;
    private static string _lastTvShowsLibraryPath = string.Empty;

    // Library cache: persisted path -> metadata so unchanged library files are not re-hashed across sessions.
    // All access guarded by _libraryLock above (no `volatile` needed - lock provides the memory barrier).
    private static Dictionary<string, CacheEntry>? _libraryCache;
    private static bool _libraryCacheDirty;

    // Source cache: maps source file paths to their fingerprint so unchanged files are not re-hashed each cycle.
    // All access guarded by _sourceCacheLock (no `volatile` needed - lock provides the memory barrier).
    private static Dictionary<string, CacheEntry>? _sourceCache;
    private static readonly object _sourceCacheLock = new();
    private static bool _sourceCacheDirty;
    private static int _sourceCachedCount;
    private static int _sourceComputedCount;

    // In-flight deduplication: if two threads race on the same source file (e.g. ImportAsync and ScanAsync
    // both classifying with a cold cache), GetOrAdd returns the same Lazy so both share one read.
    private static readonly ConcurrentDictionary<string, Lazy<string>> _sourceInFlight =
        new(StringComparer.OrdinalIgnoreCase);

    // Serialises concurrent file writes from the sync loop and the UI scan path.
    private static readonly object _cacheFileLock = new();

    private const int FingerprintChunkBytes = 64 * 1024; // 64 KB per chunk (head + tail)
    private const string SourceCacheFileName = "qbPortWeaver.mediasource.json";
    private const string LibraryCacheFileName = "qbPortWeaver.medialibrary.json";

    // Win32 ERROR_INVALID_FUNCTION wrapped as a .NET HResult. Some SMB server implementations
    // return this transiently during connection renegotiation, oplock breaks, or AV-driver
    // interception. The error clears on its own within a fraction of a second, so we retry
    // once before giving up. Distinct from a real "not supported" response (those don't recover).
    private const int ErrorInvalidFunctionHResult = unchecked((int)0x80070001);
    private const int SmbRetryDelayMs = 500;

    // Directory.Exists on an offline UNC host blocks for the OS SMB/TCP connect timeout (tens of
    // seconds). For UNC paths the check is run on a worker thread and abandoned after this budget,
    // treating a non-response as "host unreachable". A healthy host answers in a few ms over the warm
    // SMB session, so this never false-negatives a reachable host - unlike a separate TCP probe, whose
    // cold name resolution alone can exceed a short timeout.
    private const int UncExistsTimeoutMs = 5000;        // budget for a UNC Directory.Exists before giving up
    private const int UnreachableHostCacheSeconds = 30; // how long a timed-out host stays marked unreachable

    // Hosts whose bounded existence check recently timed out, keyed by host name with the UTC time of
    // the timeout. Lets multiple paths on one dead host (e.g. \\NAS\A and \\NAS\B) fail fast without
    // each spawning another blocked check.
    private static readonly ConcurrentDictionary<string, DateTime> _unreachableHosts =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions _jsonWriteOptions = new() { WriteIndented = true };

    private sealed record CacheEntry(long Size, long LastWriteTimeTicks, string Fingerprint);

    /// <summary>Attempts to create a hardlink at <paramref name="destinationPath"/> pointing to <paramref name="sourcePath"/>.
    /// Uses the <c>\\?\</c> long-path prefix to support paths longer than MAX_PATH (260 characters).</summary>
    /// <returns><see langword="true"/> if the hardlink was created; <see langword="false"/> on failure (e.g. cross-volume, unsupported filesystem).</returns>
    internal static bool TryCreateHardLink(string sourcePath, string destinationPath)
    {
        bool result = CreateHardLink(ToExtendedPath(destinationPath), ToExtendedPath(sourcePath), nint.Zero);
        if (!result)
        {
            int error = Marshal.GetLastWin32Error();
            LogManager.Instance.LogDebug($"MediaImporter.TryCreateHardLink: Failed (Win32 error {error}) - '{Path.GetFileName(sourcePath)}'", Subsystem.MediaManager);
        }
        return result;
    }

    // Verifies that two paths refer to the same file by comparing volume serial number and file index.
    // Attempts a hardlink from sourcePath to destinationPath, verifies the link identity,
    // and falls back to a file copy if the hardlink fails or the filesystem silently copies instead.
    private static void ImportWithHardlink(string sourcePath, string destinationPath)
    {
        if (TryCreateHardLink(sourcePath, destinationPath))
        {
            if (VerifyHardLink(sourcePath, destinationPath))
            {
                LogManager.Instance.LogDebug($"MediaImporter.AddFileToLibrary: Hardlinked '{Path.GetFileName(destinationPath)}'", Subsystem.MediaManager);
            }
            else
            {
                LogManager.Instance.LogMessage($"Hardlink not verified for '{Path.GetFileName(destinationPath)}' (filesystem created a copy instead), replacing with proper copy", LogLevel.Info, Subsystem.MediaManager);
                File.Delete(destinationPath);
                File.Copy(sourcePath, destinationPath, overwrite: false);
                LogManager.Instance.LogDebug($"MediaImporter.AddFileToLibrary: Copied (verified fallback) '{Path.GetFileName(destinationPath)}'", Subsystem.MediaManager);
            }
        }
        else
        {
            LogManager.Instance.LogMessage($"Hardlink failed for '{Path.GetFileName(destinationPath)}', falling back to copy", LogLevel.Info, Subsystem.MediaManager);
            File.Copy(sourcePath, destinationPath, overwrite: false);
            LogManager.Instance.LogDebug($"MediaImporter.AddFileToLibrary: Copied (fallback) '{Path.GetFileName(destinationPath)}'", Subsystem.MediaManager);
        }
    }

    // Returns false if the files have different identities (some filesystems silently create a copy instead of a hardlink).
    private static bool VerifyHardLink(string sourcePath, string destinationPath)
    {
        try
        {
            using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var destStream = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (!GetFileInformationByHandle(sourceStream.SafeFileHandle, out var sourceInfo)
                || !GetFileInformationByHandle(destStream.SafeFileHandle, out var destInfo))
            {
                return false;
            }

            return sourceInfo.VolumeSerialNumber == destInfo.VolumeSerialNumber
                && sourceInfo.FileIndexHigh == destInfo.FileIndexHigh
                && sourceInfo.FileIndexLow == destInfo.FileIndexLow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogManager.Instance.LogDebug($"MediaImporter.VerifyHardLink: Could not verify: {ex.Message}", Subsystem.MediaManager);
            return false;
        }
    }

    // Prepends the \\?\ long-path prefix so Win32 APIs accept paths longer than MAX_PATH (260).
    // UNC paths (\\server\share) become \\?\UNC\server\share; local paths become \\?\C:\...
    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    /// <summary>
    /// Adds a file to the library by transferring it from <paramref name="sourcePath"/> to <paramref name="destinationPath"/> using the specified <paramref name="importMode"/>.
    /// Creates the target directory if needed. Skips files that already exist at the destination with the same fingerprint.
    /// In <see cref="ImportMode.Hardlink"/> mode, automatically falls back to copy if the hardlink fails.
    /// </summary>
    internal static void AddFileToLibrary(string sourcePath, string destinationPath, ImportMode importMode)
    {
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (DestinationMatchesSource(sourcePath, destinationPath))
        {
            LogManager.Instance.LogDebug($"MediaImporter.AddFileToLibrary: Skipped '{Path.GetFileName(destinationPath)}' - target already exists with same fingerprint", Subsystem.MediaManager);
            return;
        }

        // Destination exists but different content: two different source files resolved to the same target path
        if (File.Exists(destinationPath))
        {
            LogDestinationConflict(sourcePath, destinationPath);
            return;
        }

        var targetDir = Path.GetDirectoryName(destinationPath);
        try
        {
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            switch (importMode)
            {
                case ImportMode.Hardlink:
                    ImportWithHardlink(sourcePath, destinationPath);
                    break;

                case ImportMode.Copy:
                    File.Copy(sourcePath, destinationPath, overwrite: false);
                    LogManager.Instance.LogDebug($"MediaImporter.AddFileToLibrary: Copied '{Path.GetFileName(destinationPath)}'", Subsystem.MediaManager);
                    break;

                case ImportMode.Move:
                    File.Move(sourcePath, destinationPath);
                    LogManager.Instance.LogDebug($"MediaImporter.AddFileToLibrary: Moved '{Path.GetFileName(destinationPath)}'", Subsystem.MediaManager);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(importMode), importMode, "Unsupported import mode");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            LogManager.Instance.LogMessage($"Failed to import '{Path.GetFileName(sourcePath)}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
            return;
        }

        AddToLibraryIndex(destinationPath);
    }

    // Returns the file's size as a display string ("12345 bytes"), or "unknown size" if it cannot be
    // read. Never throws: it only backs the destination-conflict warning, whose job is to describe a
    // skip - a transient SMB stat failure or a file that vanished must not abort the import path.
    internal static string DescribeFileSize(string path)
    {
        try
        {
            return $"{new FileInfo(path).Length} bytes";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unknown size";
        }
    }

    /// <summary>Logs the standard "destination already exists with different content, skipping to avoid
    /// overwrite" warning. Shared by the dry-run-aware import front door (<see cref="MediaManagerService.ImportFile"/>)
    /// and the direct library-add path (<see cref="AddFileToLibrary"/>), which each keep their own
    /// <see cref="File.Exists(string)"/> guard but emit one identical message.</summary>
    internal static void LogDestinationConflict(string sourcePath, string destinationPath) =>
        LogManager.Instance.LogMessage(
            $"Destination conflict: '{Path.GetFileName(destinationPath)}' already exists with different content " +
            $"(source: {DescribeFileSize(sourcePath)}, dest: {DescribeFileSize(destinationPath)}). Skipping to avoid overwriting.",
            LogLevel.Warn, Subsystem.MediaManager);

    /// <summary>Returns <see langword="true"/> if the destination file already exists and its fingerprint matches the source - i.e. the source was already imported.</summary>
    internal static bool DestinationMatchesSource(string sourcePath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
            return false;

        try
        {
            return ComputeFingerprint(sourcePath) == ComputeFingerprint(destinationPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fingerprinting failed (typically a transient SMB hiccup). Fall back to size comparison.
            // The fallback can also throw if the file became inaccessible between the calls, so guard
            // it too - returning false makes the caller attempt the import, which has its own IO
            // protections (AddFileToLibrary catches IOException, and the "destination conflict"
            // check re-tests File.Exists). Better to retry the import than abort the whole category.
            try
            {
                var sourceInfo = new FileInfo(sourcePath);
                var destInfo = new FileInfo(destinationPath);
                return sourceInfo.Length == destInfo.Length;
            }
            catch (Exception fallbackEx) when (fallbackEx is IOException or UnauthorizedAccessException)
            {
                LogManager.Instance.LogDebug(
                    $"MediaImporter.DestinationMatchesSource: Both fingerprint and length check failed for '{Path.GetFileName(sourcePath)}' ({ex.GetType().Name}, then {fallbackEx.GetType().Name}: {fallbackEx.Message})",
                    Subsystem.MediaManager);
                return false;
            }
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if the file is ready to process.
    /// If the file's size and last-write timestamp match the source cache it was confirmed write-complete
    /// on the previous scan and is approved without opening the file (no network round-trip).
    /// Files not in the cache fall back to <see cref="IsFileWriteComplete"/>.
    /// <para>Callers should supply a <see cref="FileInfo"/> obtained from <see cref="DirectoryInfo.EnumerateFiles(string,EnumerationOptions)"/>
    /// so that <see cref="FileInfo.Length"/> and <see cref="FileInfo.LastWriteTimeUtc"/> are already
    /// populated from the directory listing and do not trigger additional I/O.</para>
    /// </summary>
    internal static bool IsFileReadyForImport(FileInfo fi)
    {
        // Read _sourceCache INSIDE the lock: a snapshot captured outside the lock could be
        // orphaned by a concurrent ClearAllCaches, leading to reads from a dictionary no
        // longer referenced by the live state.
        lock (_sourceCacheLock)
        {
            if (_sourceCache is not null
                && _sourceCache.TryGetValue(fi.FullName, out var entry)
                && entry.Size == fi.Length
                && entry.LastWriteTimeTicks == fi.LastWriteTimeUtc.Ticks)
                return true;
        }
        return IsFileWriteComplete(fi.FullName);
    }

    /// <summary>
    /// Returns <see langword="true"/> if no other process currently holds a write-capable handle to the file.
    /// Opens with <see cref="FileAccess.Read"/> and <see cref="FileShare.Read"/>: the open fails with
    /// <see cref="IOException"/> (sharing violation) whenever any other handle on the file permits writes,
    /// which is the case while the file is still being written. Processes that opened the file read-only
    /// allow concurrent reads and pass this check.
    /// <see cref="UnauthorizedAccessException"/> is treated as complete: the file exists but we lack
    /// permission (e.g. read-only attribute, network share ACL) and is not being actively written to.
    /// </summary>
    internal static bool IsFileWriteComplete(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Computes a lightweight fingerprint for a file: the file size combined with a SHA-256 hash of the first
    /// and last 64 KB. For files up to 128 KB the entire content is hashed. Reading only 128 KB keeps this fast
    /// even for multi-gigabyte video files on spinning disks or network shares.
    /// </summary>
    internal static string ComputeFingerprint(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long size = stream.Length;
        if (size == 0) return "0";

        byte[] data;
        if (size <= FingerprintChunkBytes * 2)
        {
            data = new byte[size];
            stream.ReadExactly(data);
        }
        else
        {
            data = new byte[FingerprintChunkBytes * 2];
            stream.ReadExactly(data, 0, FingerprintChunkBytes);
            stream.Seek(-FingerprintChunkBytes, SeekOrigin.End);
            stream.ReadExactly(data, FingerprintChunkBytes, FingerprintChunkBytes);
        }

        return $"{size}:{Convert.ToHexString(SHA256.HashData(data))}";
    }

    // Decides whether the previously-built library index can be reused this cycle. Returns true
    // when the cached index is valid and the cycle counter has been bumped (caller skips the
    // rebuild). Returns false when a full rebuild is required, in which case the path tracking
    // fields and cycle counter are reset so the rebuild starts from a clean state.
    // Caller holds _libraryBuildSemaphore.
    private static bool TryReuseCachedIndex(string moviesLibraryPath, string tvShowsLibraryPath, bool allowReuse)
    {
        bool pathsChanged = !string.Equals(moviesLibraryPath, _lastMoviesLibraryPath, StringComparison.OrdinalIgnoreCase)
                         || !string.Equals(tvShowsLibraryPath, _lastTvShowsLibraryPath, StringComparison.OrdinalIgnoreCase);
        bool forceRebuild = pathsChanged || _libraryBuildCycleCount >= FullRebuildIntervalCycles;
        if (!forceRebuild && allowReuse && _libraryFingerprints is not null)
        {
            LogManager.Instance.LogDebug($"MediaImporter.BuildLibraryIndexAsync: Reusing index (cycle {_libraryBuildCycleCount}/{FullRebuildIntervalCycles})", Subsystem.MediaManager);
            _libraryBuildCycleCount++;
            return true;
        }
        if (pathsChanged && _libraryFingerprints is not null)
            LogManager.Instance.LogDebug("MediaImporter.BuildLibraryIndexAsync: Library paths changed, forcing full rebuild", Subsystem.MediaManager);
        else if (_libraryBuildCycleCount >= FullRebuildIntervalCycles)
            LogManager.Instance.LogDebug($"MediaImporter.BuildLibraryIndexAsync: Forcing periodic rebuild (every {FullRebuildIntervalCycles} cycles)", Subsystem.MediaManager);
        _libraryBuildCycleCount = 1; // Reset to 1, not 0 - this rebuild counts as the first cycle
        _lastMoviesLibraryPath = moviesLibraryPath;
        _lastTvShowsLibraryPath = tvShowsLibraryPath;
        return false;
    }

    /// <summary>
    /// Walks the library folders and builds a fingerprint index so <see cref="IsAlreadyInLibrary"/> can detect
    /// files that were previously imported (regardless of the name they were imported under).
    /// Uses a persisted cache so only new or modified library files are fingerprinted; deleted files are pruned.
    /// If called concurrently (e.g. from both the sync loop and a UI scan), the second caller waits for the
    /// first to finish. When <paramref name="allowReuse"/> is true, the existing index is reused for up to
    /// <see cref="FullRebuildIntervalCycles"/> cycles before forcing a full rebuild to catch external changes.
    /// Returns <see langword="true"/> when the index reflects the configured library state and is safe to
    /// query. Returns <see langword="false"/> when the configured paths were inaccessible or one or more
    /// folder enumerations failed: in that case the prior <see cref="_libraryFingerprints"/> and the on-disk
    /// cache are left untouched, and the caller must skip the import cycle - committing a partial index
    /// would falsely report library files as missing and risk creating duplicates.
    /// </summary>
    internal static async Task<bool> BuildLibraryIndexAsync(string moviesLibraryPath, string tvShowsLibraryPath, bool allowReuse = false, CancellationToken cancellationToken = default)
    {
        await _libraryBuildSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryReuseCachedIndex(moviesLibraryPath, tvShowsLibraryPath, allowReuse))
                return true;

            int configuredCount = (string.IsNullOrWhiteSpace(moviesLibraryPath) ? 0 : 1)
                                + (string.IsNullOrWhiteSpace(tvShowsLibraryPath) ? 0 : 1);
            var libraryPaths = new[] { moviesLibraryPath, tvShowsLibraryPath }
                .Where(p => !string.IsNullOrWhiteSpace(p) && DirectoryExistsWithSmbRetry(p))
                .ToArray();

            // If ANY configured path is inaccessible, skip the whole build rather than committing a
            // partial index - a partial fingerprint set would report files under the missing path as
            // absent and risk duplicate imports. Force a rebuild next cycle.
            if (configuredCount > 0 && libraryPaths.Length < configuredCount)
            {
                LogManager.Instance.LogMessage("Library index build skipped: one or more configured paths not accessible - will retry next cycle", LogLevel.Warn, Subsystem.MediaManager);
                _libraryBuildCycleCount = FullRebuildIntervalCycles;
                return false;
            }

            LogManager.Instance.LogMessage($"Building library index across {libraryPaths.Length} folder(s)", LogLevel.Info, Subsystem.MediaManager);
            LoadLibraryCache();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            var fpToPath = new Dictionary<string, string>(StringComparer.Ordinal);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int cached = 0, computed = 0;
            bool anyEnumerationFailed = false;

            foreach (var path in libraryPaths)
            {
                LogManager.Instance.LogMessage($"Enumerating library folder: '{path}'", LogLevel.Info, Subsystem.MediaManager);
                List<FileInfo> files;
                try { files = EnumerateLibraryFolder(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped library folder '{path}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                    anyEnumerationFailed = true;
                    continue;
                }

                var (c, comp) = FingerprintLibraryFiles(files, fingerprints, fpToPath, seenPaths, cancellationToken);
                cached += c;
                computed += comp;
            }

            // Any folder enumeration failure poisons the index: a partial fingerprint set would
            // falsely report files in the failed folder as missing from the library, causing
            // duplicate imports on the next cycle. Preserve the prior _libraryFingerprints (or
            // leave it null on first run) and skip PruneLibraryCache so the on-disk cache is
            // not wiped by an empty seenPaths set. Force a rebuild next cycle.
            if (anyEnumerationFailed)
            {
                LogManager.Instance.LogMessage(
                    "Library index build incomplete - one or more configured folders failed to enumerate; will retry next cycle",
                    LogLevel.Warn, Subsystem.MediaManager);
                _libraryBuildCycleCount = FullRebuildIntervalCycles;
                return false;
            }

            PruneLibraryCache(seenPaths);

            sw.Stop();
            lock (_libraryLock) { _libraryFingerprints = fingerprints; }
            LogManager.Instance.LogMessage(
                $"Library index built: {fingerprints.Count} files in {sw.ElapsedMilliseconds}ms (cached={cached}, computed={computed})",
                LogLevel.Info, Subsystem.MediaManager);
            return true;
        }
        finally
        {
            _libraryBuildSemaphore.Release();
        }
    }

    // Loads the library cache from disk. Initialises an empty cache on first run or if the file is corrupt.
    private static void LoadLibraryCache()
    {
        if (_libraryCache is not null) return;
        _libraryCache = LoadCacheFromDisk(LibraryCacheFileName, "Library cache");
        _libraryCacheDirty = false;
    }

    // Loads a JSON-persisted cache from disk, returning an empty cache on missing file or corruption.
    private static Dictionary<string, CacheEntry> LoadCacheFromDisk(string fileName, string label)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var filePath = GetCacheFilePath(fileName);
            if (!File.Exists(filePath))
            {
                sw.Stop();
                LogManager.Instance.LogMessage($"{label} loaded: 0 entries in {sw.ElapsedMilliseconds}ms", LogLevel.Info, Subsystem.MediaManager);
                return new(StringComparer.OrdinalIgnoreCase);
            }

            var json = File.ReadAllText(filePath);
            var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
            var cache = entries is not null
                ? new Dictionary<string, CacheEntry>(entries, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);

            sw.Stop();
            LogManager.Instance.LogMessage($"{label} loaded: {cache.Count} entries in {sw.ElapsedMilliseconds}ms", LogLevel.Info, Subsystem.MediaManager);
            return cache;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogManager.Instance.LogMessage($"{label} could not be loaded, starting fresh: {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    // Enumerates all files in a library folder.
    // FileInfo metadata (Length, LastWriteTimeUtc) comes from the directory listing - no extra stat per file.
    // IgnoreInaccessible = true silently skips permission-denied folders.
    // No MaxRecursionDepth: Plex libraries are shallow by convention (Title/Season/file), so no cap needed.
    private static List<FileInfo> EnumerateLibraryFolder(string folder) =>
        InvokeWithSmbRetry(folder, () =>
            new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            }).ToList());

    /// <summary>
    /// Runs an SMB-touching action, retrying once with a short delay if it throws
    /// <see cref="IOException"/> with HResult <c>ERROR_INVALID_FUNCTION</c> (the throwing
    /// variant of the transient SMB hiccup). A second failure propagates to the caller's catch.
    /// Used by every Media Manager call that enumerates or lists an SMB path.
    /// </summary>
    internal static T InvokeWithSmbRetry<T>(string folder, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (IOException ex) when (ex.HResult == ErrorInvalidFunctionHResult)
        {
            LogManager.Instance.LogDebug(
                $"MediaImporter.InvokeWithSmbRetry: '{folder}' hit ERROR_INVALID_FUNCTION (transient SMB error), retrying after {SmbRetryDelayMs}ms",
                Subsystem.MediaManager);
            Thread.Sleep(SmbRetryDelayMs);
            return action();
        }
    }

    /// <summary>
    /// Verifies a path exists, retrying once after a short delay when the first attempt is inconclusive -
    /// covering both the transient "silent-false" SMB hiccup (a reachable path momentarily reported
    /// missing) and a transient slow response (different symptom from <see cref="InvokeWithSmbRetry"/>
    /// which handles throws). For UNC paths the underlying <see cref="Directory.Exists(string)"/> is
    /// bounded by <see cref="UncExistsTimeoutMs"/> so an offline host fails fast instead of blocking on
    /// the OS SMB/TCP connect timeout. Only when both attempts time out is the host cached as unreachable
    /// for <see cref="UnreachableHostCacheSeconds"/>s, so a single slow check does not falsely skip a
    /// healthy host. Used by every Media Manager call that verifies an SMB path before acting on it.
    /// </summary>
    internal static bool DirectoryExistsWithSmbRetry(string folder)
    {
        bool isUnc = TryGetUncHost(folder, out var host);

        // Fast-fail if this UNC host's existence check timed out twice recently (host offline).
        if (isUnc && IsHostCachedUnreachable(host))
        {
            LogManager.Instance.LogDebug(
                $"MediaImporter.DirectoryExistsWithSmbRetry: Host for '{folder}' is unreachable (cached), skipping existence check",
                Subsystem.MediaManager);
            return false;
        }

        var first = DirectoryExistsBounded(folder, isUnc);
        if (first == true) return true;

        // first is false (reachable but reported missing) or null (timed out). Retry once after a short
        // delay - a transient silent-false or a transient slow response usually clears on the second try.
        LogManager.Instance.LogDebug(
            $"MediaImporter.DirectoryExistsWithSmbRetry: '{folder}' not confirmed ({(first is null ? "no response" : "reported not found")}), retrying after {SmbRetryDelayMs}ms",
            Subsystem.MediaManager);
        Thread.Sleep(SmbRetryDelayMs);
        var second = DirectoryExistsBounded(folder, isUnc);
        if (second == true) return true;

        // Still unconfirmed. If a UNC check timed out on both attempts the host is unreachable - cache it
        // so sibling paths and later cycles fail fast instead of each waiting out the timeout.
        if (isUnc && first is null && second is null)
        {
            _unreachableHosts[host] = DateTime.UtcNow;
            LogManager.Instance.LogDebug(
                $"MediaImporter.DirectoryExistsWithSmbRetry: '{folder}' did not respond within {UncExistsTimeoutMs}ms on two attempts, treating host as unreachable",
                Subsystem.MediaManager);
        }
        return false;
    }

    // Runs Directory.Exists, bounding UNC paths by UncExistsTimeoutMs (a non-UNC path is checked
    // directly - it never blocks). Returns the result, or null when a UNC check timed out. Side-effect
    // free apart from observing the abandoned task: Directory.Exists itself does not throw (it returns
    // false on error), so the orphaned task completes harmlessly; the continuation only guards an
    // unexpected fault from becoming an unobserved exception. Caching a timed-out host is left to the
    // caller so it can retry before deciding the host is unreachable.
    private static bool? DirectoryExistsBounded(string folder, bool isUnc)
    {
        if (!isUnc) return Directory.Exists(folder);

        var task = Task.Run(() => Directory.Exists(folder));
        if (task.Wait(UncExistsTimeoutMs)) return task.Result;

        _ = task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        return null;
    }

    // True if the host was marked unreachable within the cache window; clears a stale entry so the
    // next check probes fresh.
    private static bool IsHostCachedUnreachable(string host)
    {
        if (!_unreachableHosts.TryGetValue(host, out var failedAt)) return false;
        if ((DateTime.UtcNow - failedAt).TotalSeconds < UnreachableHostCacheSeconds) return true;
        _unreachableHosts.TryRemove(host, out _); // stale - allow a fresh check
        return false;
    }

    // Extracts the host from a UNC path (\\host\share\... or //host/share/...). Returns false for
    // local paths (drive letters, relative paths), which are never bounded or cached.
    private static bool TryGetUncHost(string path, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrEmpty(path) || path.Length < 3) return false;
        if (!IsSlash(path[0]) || !IsSlash(path[1])) return false;

        int end = path.IndexOfAny(['\\', '/'], 2);
        host = end < 0 ? path[2..] : path[2..end];

        // \\?\ (extended-length) and \\.\ (device) prefixes are not network hosts - treat as local
        // so they are checked directly rather than bounded or cached.
        if (host is "?" or ".")
        {
            host = string.Empty;
            return false;
        }

        return host.Length > 0;

        static bool IsSlash(char c) => c is '\\' or '/';
    }

    // Fingerprints a pre-enumerated list of library files in parallel, using the cache where possible.
    // Each file requires a 128 KB read; reads are bounded by FingerprintParallelism.
    // fpToPath maps each fingerprint to the first library path that produced it; additional copies are warned.
    private static (int Cached, int Computed) FingerprintLibraryFiles(
        List<FileInfo> files, HashSet<string> fingerprints, Dictionary<string, string> fpToPath,
        HashSet<string> seenPaths, CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<(string FullName, string Fingerprint, bool WasCached)>();
        var seen = new ConcurrentBag<string>();

        Parallel.ForEach(files,
            new ParallelOptions { MaxDegreeOfParallelism = FingerprintParallelism, CancellationToken = cancellationToken },
            fi =>
            {
                seen.Add(fi.FullName);
                try
                {
                    string fp = GetOrComputeLibraryFingerprint(fi, out bool wasCached);
                    results.Add((fi.FullName, fp, wasCached));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogDebug($"MediaImporter.FingerprintLibraryFiles: Skipped '{fi.Name}': {ex.Message}", Subsystem.MediaManager);
                }
            });

        // Merge into caller-provided collections (single-threaded; no concurrent access from here)
        int cached = 0, computed = 0;
        foreach (var (fullName, fp, wasCached) in results)
        {
            if (!fpToPath.TryAdd(fp, fullName))
                LogManager.Instance.LogMessage(
                    $"Library duplicate: '{fullName}' has same content as '{fpToPath[fp]}'",
                    LogLevel.Warn, Subsystem.MediaManager);
            fingerprints.Add(fp);
            if (wasCached) cached++; else computed++;
        }
        foreach (var p in seen)
            seenPaths.Add(p);

        return (cached, computed);
    }

    // Returns a cached library fingerprint if metadata matches, otherwise computes and caches it.
    // Reads _libraryCache INSIDE the lock so a concurrent ClearAllCaches cannot orphan the
    // captured reference between the field read and the dictionary access.
    private static string GetOrComputeLibraryFingerprint(FileInfo fi, out bool wasCached)
    {
        lock (_libraryLock)
        {
            if (_libraryCache is not null
                && _libraryCache.TryGetValue(fi.FullName, out var entry)
                && entry.Size == fi.Length
                && entry.LastWriteTimeTicks == fi.LastWriteTimeUtc.Ticks)
            {
                wasCached = true;
                return entry.Fingerprint;
            }
        }

        string fp = ComputeFingerprint(fi.FullName);
        lock (_libraryLock)
        {
            // Re-check inside the lock: ClearAllCaches may have nulled _libraryCache while we
            // were computing the fingerprint. Skip the write in that case rather than recreating
            // a dictionary that the live state no longer references.
            if (_libraryCache is not null)
            {
                _libraryCache[fi.FullName] = new CacheEntry(fi.Length, fi.LastWriteTimeUtc.Ticks, fp);
                _libraryCacheDirty = true;
            }
        }
        wasCached = false;
        return fp;
    }

    // Removes library cache entries for files not present in the current directory walk.
    // Caller holds _libraryBuildSemaphore so the cache won't be racing with another rebuild,
    // but ClearAllCaches can still null _libraryCache concurrently - treat that as a no-op.
    // Reading _libraryCache inside the lock matches the locking discipline used elsewhere
    // in this file (GetOrComputeLibraryFingerprint, IsFileReadyForImport).
    private static void PruneLibraryCache(HashSet<string> seenPaths)
    {
        List<string> stale;
        lock (_libraryLock)
        {
            if (_libraryCache is null) return;
            stale = _libraryCache.Keys.Where(k => !seenPaths.Contains(k)).ToList();
            if (stale.Count == 0) return;

            foreach (var key in stale)
                _libraryCache.Remove(key);
            _libraryCacheDirty = true;
        }

        LogManager.Instance.LogDebug($"MediaImporter.PruneLibraryCache: Removed {stale.Count} stale entries", Subsystem.MediaManager);
    }

    /// <summary>Adds a file's fingerprint to the library index and cache after a successful import. No-op if the index hasn't been built yet.</summary>
    internal static void AddToLibraryIndex(string importedFilePath)
    {
        try
        {
            var fi = new FileInfo(importedFilePath);
            string fingerprint = ComputeFingerprint(importedFilePath);
            lock (_libraryLock)
            {
                if (_libraryFingerprints is null) return;
                _libraryFingerprints.Add(fingerprint);

                var cache = _libraryCache;
                if (cache is not null)
                {
                    cache[importedFilePath] = new CacheEntry(fi.Length, fi.LastWriteTimeUtc.Ticks, fingerprint);
                    _libraryCacheDirty = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LogManager.Instance.LogDebug($"MediaImporter.AddToLibraryIndex: Could not index '{Path.GetFileName(importedFilePath)}': {ex.Message}", Subsystem.MediaManager);
        }
    }

    /// <summary>Returns <see langword="true"/> if a file with the same fingerprint already exists somewhere in the library.</summary>
    /// <param name="fi">
    /// Prefer passing a <see cref="FileInfo"/> obtained from a directory enumeration so that
    /// <see cref="FileInfo.Length"/> and <see cref="FileInfo.LastWriteTimeUtc"/> are already populated
    /// and no additional SMB stat call is required.
    /// </param>
    internal static bool IsAlreadyInLibrary(FileInfo fi)
    {
        try
        {
            return IsAlreadyInLibrary(GetOrComputeSourceFingerprint(fi));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogManager.Instance.LogDebug($"MediaImporter.IsAlreadyInLibrary: Could not read '{fi.Name}': {ex.Message}", Subsystem.MediaManager);
            return false;
        }
    }

    /// <summary>Returns <see langword="true"/> if the given fingerprint is already present in the library index.</summary>
    internal static bool IsAlreadyInLibrary(string fingerprint)
    {
        lock (_libraryLock)
            return _libraryFingerprints?.Contains(fingerprint) ?? false;
    }

    // Returns the fingerprint for a source file, using the in-memory cache when the file has not changed.
    // Uses _sourceInFlight to deduplicate concurrent computation: if two threads race on the same file,
    // the second waits on the Lazy rather than issuing a redundant read.
    // Cold-start constraint: when _sourceCache is null (first ever run, no persisted cache), the dedup
    // guarantee does not apply - concurrent callers skip the in-flight path and each compute independently.
    internal static string GetOrComputeSourceFingerprint(FileInfo fi)
    {
        // First lock pass: cache hit check. _sourceCache is read inside the lock so a concurrent
        // ClearAllCaches cannot orphan a captured reference here.
        bool cacheActive;
        lock (_sourceCacheLock)
        {
            cacheActive = _sourceCache is not null;
            if (cacheActive
                && _sourceCache!.TryGetValue(fi.FullName, out var entry)
                && entry.Size == fi.Length
                && entry.LastWriteTimeTicks == fi.LastWriteTimeUtc.Ticks)
            {
                Interlocked.Increment(ref _sourceCachedCount);
                return entry.Fingerprint;
            }
        }

        if (!cacheActive)
        {
            Interlocked.Increment(ref _sourceComputedCount);
            return ComputeFingerprint(fi.FullName);
        }

        // Create before GetOrAdd so we can tell whether this thread won the race.
        // Same cancellation-cascade race window applies as TmdbCacheManager.GetOrComputeAsync
        // (see comment there): if the winning thread's compute throws between fault and eviction,
        // a racing GetOrAdd may share the already-faulted Lazy. Accepted as documented behavior.
        var newLazy = new Lazy<string>(() => ComputeFingerprint(fi.FullName));
        var lazy = _sourceInFlight.GetOrAdd(fi.FullName, newLazy);
        string fp;
        try
        {
            fp = lazy.Value;
        }
        catch
        {
            // Remove the failed Lazy so the file can be retried on the next cycle.
            _sourceInFlight.TryRemove(new KeyValuePair<string, Lazy<string>>(fi.FullName, lazy));
            throw;
        }
        // Remove only our Lazy instance so a newer entry for the same path is left untouched.
        _sourceInFlight.TryRemove(new KeyValuePair<string, Lazy<string>>(fi.FullName, lazy));

        // Winner computed the fingerprint; loser waited on the Lazy and reused the result.
        if (ReferenceEquals(lazy, newLazy))
            Interlocked.Increment(ref _sourceComputedCount);
        else
            Interlocked.Increment(ref _sourceCachedCount);

        // Re-check inside the lock: ClearAllCaches may have nulled _sourceCache while we
        // were computing. Skip the write rather than persisting into an orphaned dictionary.
        lock (_sourceCacheLock)
        {
            if (_sourceCache is not null)
            {
                _sourceCache[fi.FullName] = new CacheEntry(fi.Length, fi.LastWriteTimeUtc.Ticks, fp);
                _sourceCacheDirty = true;
            }
        }
        return fp;
    }

    /// <summary>Returns the number of source fingerprints served from cache and the number freshly computed during the current scan cycle.</summary>
    internal static (int Cached, int Computed) GetSourceCacheStats() =>
        (_sourceCachedCount, _sourceComputedCount);

    /// <summary>
    /// Loads the source cache from disk. No-op if already loaded.
    /// The cache maps source file paths to their fingerprints so unchanged files are not re-hashed on subsequent cycles.
    /// </summary>
    internal static void LoadSourceCache()
    {
        // Reset per-cycle scan stats (even if cache is already in memory from a prior cycle)
        Interlocked.Exchange(ref _sourceCachedCount, 0);
        Interlocked.Exchange(ref _sourceComputedCount, 0);

        if (_sourceCache is not null) return;
        _sourceCache = LoadCacheFromDisk(SourceCacheFileName, "Source cache");
        _sourceCacheDirty = false;
    }

    /// <summary>
    /// Persists the source cache to disk, pruning entries for files that no longer exist.
    /// No-op if the cache has not changed since the last save (or load).
    /// </summary>
    internal static void SaveSourceCache()
    {
        try
        {
            Dictionary<string, CacheEntry> snapshot;
            bool wasDirty;
            lock (_sourceCacheLock)
            {
                // Read _sourceCache INSIDE the lock (same discipline as the readers above):
                // a reference captured outside could be orphaned by a concurrent ClearAllCaches,
                // and saving it would resurrect the just-deleted cache file.
                if (_sourceCache is null) return;
                snapshot = new Dictionary<string, CacheEntry>(_sourceCache, StringComparer.OrdinalIgnoreCase);
                wasDirty = _sourceCacheDirty;
                _sourceCacheDirty = false; // reset inside lock so concurrent writes after this point re-set it
            }

            // If every cached entry was visited this cycle, none can be stale - skip File.Exists entirely.
            // If visited==0, the source share was unreachable this cycle; treat as no stale entries to
            // avoid calling File.Exists on hundreds of UNC paths against an unreachable host (each call
            // blocks for a full SMB timeout, turning a missed cycle into a multi-minute hang).
            int visited = _sourceCachedCount + _sourceComputedCount;
            bool mightHaveStaleEntries = visited > 0 && snapshot.Count > visited;

            if (!wasDirty && !mightHaveStaleEntries) return;

            var toSave = mightHaveStaleEntries
                ? snapshot.Where(kv => File.Exists(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                : snapshot;

            WriteCacheToDisk(toSave, SourceCacheFileName, "SaveSourceCache");
        }
        catch (Exception ex)
        {
            // Restore the dirty flag so the next cycle retries the save.
            lock (_sourceCacheLock) _sourceCacheDirty = true;
            LogManager.Instance.LogMessage($"Failed to save source cache: {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
        }
    }

    /// <summary>
    /// Persists the library cache to disk.
    /// No-op if the cache has not changed since the last save (or load).
    /// </summary>
    internal static void SaveLibraryCache()
    {
        try
        {
            Dictionary<string, CacheEntry> snapshot;
            bool wasDirty;
            lock (_libraryLock)
            {
                // Read _libraryCache INSIDE the lock - see SaveSourceCache for the rationale.
                if (_libraryCache is null) return;
                snapshot = new Dictionary<string, CacheEntry>(_libraryCache, StringComparer.OrdinalIgnoreCase);
                wasDirty = _libraryCacheDirty;
                _libraryCacheDirty = false; // reset inside lock so concurrent writes after this point re-set it
            }

            if (!wasDirty) return;

            WriteCacheToDisk(snapshot, LibraryCacheFileName, "SaveLibraryCache");
        }
        catch (Exception ex)
        {
            // Restore the dirty flag so the next cycle retries the save.
            lock (_libraryLock) _libraryCacheDirty = true;
            LogManager.Instance.LogMessage($"Failed to save library cache: {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
        }
    }

    // Serialises and atomically writes a cache snapshot to disk.
    private static void WriteCacheToDisk(IDictionary<string, CacheEntry> entries, string fileName, string debugLabel)
    {
        var json = JsonSerializer.Serialize(entries, _jsonWriteOptions);
        lock (_cacheFileLock)
            AppConstants.WriteAtomic(GetCacheFilePath(fileName), json);
        LogManager.Instance.LogDebug($"MediaImporter.{debugLabel}: Saved {entries.Count} entries", Subsystem.MediaManager);
    }

    /// <summary>
    /// Deletes both the source and library fingerprint caches from disk and clears the in-memory state,
    /// forcing a full re-hash on the next scan or sync cycle.
    /// </summary>
    internal static void ClearAllCaches()
    {
        lock (_sourceCacheLock)
        {
            _sourceCache = null;
            _sourceCacheDirty = false;
        }
        _sourceInFlight.Clear();

        lock (_libraryLock)
        {
            _libraryFingerprints = null;
            _libraryCache = null;
            _libraryCacheDirty = false;
            _libraryBuildCycleCount = 0;
            _lastMoviesLibraryPath = string.Empty;
            _lastTvShowsLibraryPath = string.Empty;
        }

        AppConstants.DeleteFileSafely(GetCacheFilePath(SourceCacheFileName));
        AppConstants.DeleteFileSafely(GetCacheFilePath(LibraryCacheFileName));

        LogManager.Instance.LogMessage("Fingerprint caches cleared", LogLevel.Info, Subsystem.MediaManager);
    }

    internal static string GetCacheFilePath(string fileName) =>
        AppConstants.GetDataFilePath(fileName);

    // CreateHardLink: lpFileName is the NEW link (destination), lpExistingFileName is the existing file (source).
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    // Plain two-int struct matching the Win32 FILETIME layout.
    // Replaces ComTypes.FILETIME so the [LibraryImport] source generator can analyse
    // ByHandleFileInformation as a fully blittable type (ComTypes.FILETIME blocks SYSLIB1051).
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public int Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
