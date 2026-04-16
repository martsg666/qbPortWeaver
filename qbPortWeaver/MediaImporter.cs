using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace qbPortWeaver
{
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

        // Library cache: persisted path -> metadata so unchanged library files are not re-hashed across sessions.
        private static Dictionary<string, CacheEntry>? _libraryCache;
        private static volatile bool _libraryCacheDirty;

        // Source cache: maps source file paths to their fingerprint so unchanged files are not re-hashed each cycle.
        private static Dictionary<string, CacheEntry>? _sourceCache;
        private static readonly object _sourceCacheLock = new();
        private static volatile bool _sourceCacheDirty;
        private static int _sourceCachedCount;
        private static int _sourceComputedCount;

        // In-flight deduplication: if two threads race on the same source file (e.g. ImportAsync and ScanAsync
        // both classifying with a cold cache), GetOrAdd returns the same Lazy so both share one read.
        private static readonly ConcurrentDictionary<string, Lazy<string>> _sourceInFlight =
            new(StringComparer.OrdinalIgnoreCase);

        // Serialises concurrent file writes from the sync loop and the UI scan path.
        private static readonly object _cacheFileLock = new();

        private const int FingerprintChunkBytes = 64 * 1024; // 64 KB per chunk (head + tail)
        private const string SourceCacheFileName  = "qbPortWeaver.mediasource.json";
        private const string LibraryCacheFileName = "qbPortWeaver.medialibrary.json";

        private static readonly JsonSerializerOptions _jsonWriteOptions = new() { WriteIndented = true };

        private sealed record CacheEntry(long Size, long LastWriteTimeTicks, string Fingerprint);

        /// <summary>Attempts to create a hardlink at <paramref name="destinationPath"/> pointing to <paramref name="sourcePath"/>.
        /// Uses the extended-length path prefix to support paths longer than MAX_PATH (260 characters).</summary>
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
                using var destStream   = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

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

        // Prepends the extended-length prefix so Win32 APIs accept paths longer than MAX_PATH (260).
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

            if (IsDuplicateFile(sourcePath, destinationPath))
            {
                LogManager.Instance.LogDebug($"MediaImporter.AddFileToLibrary: Skipped '{Path.GetFileName(destinationPath)}' - target already exists with same fingerprint", Subsystem.MediaManager);
                return;
            }

            // Destination exists but different content: two different source files resolved to the same target path
            if (File.Exists(destinationPath))
            {
                LogManager.Instance.LogMessage(
                    $"Destination conflict: '{Path.GetFileName(destinationPath)}' already exists with different content (source: {new FileInfo(sourcePath).Length} bytes, dest: {new FileInfo(destinationPath).Length} bytes). Skipping to avoid overwriting.",
                    LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            var targetDir = Path.GetDirectoryName(destinationPath);
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

            AddToLibraryIndex(destinationPath);
        }

        /// <summary>Returns <see langword="true"/> if the destination file already exists and has the same fingerprint as the source.</summary>
        internal static bool IsDuplicateFile(string sourcePath, string destinationPath)
        {
            if (!File.Exists(destinationPath))
                return false;

            try
            {
                return ComputeFingerprint(sourcePath) == ComputeFingerprint(destinationPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // If fingerprinting fails, fall back to size comparison to avoid silently skipping the import
                var sourceInfo = new FileInfo(sourcePath);
                var destInfo   = new FileInfo(destinationPath);
                return sourceInfo.Length == destInfo.Length;
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
            var cache = _sourceCache;
            if (cache is not null)
            {
                lock (_sourceCacheLock)
                {
                    if (cache.TryGetValue(fi.FullName, out var entry)
                        && entry.Size == fi.Length
                        && entry.LastWriteTimeTicks == fi.LastWriteTimeUtc.Ticks)
                        return true;
                }
            }
            return IsFileWriteComplete(fi.FullName);
        }

        /// <summary>
        /// Returns <see langword="true"/> if the file is not exclusively locked for writing by another process.
        /// Uses <see cref="FileAccess.Read"/> and <see cref="FileShare.ReadWrite"/>: processes that hold a read
        /// handle allow concurrent reads, so those files pass this check. Only a write-exclusive lock
        /// (<see cref="FileShare.None"/> on the writer's handle) causes the open to fail, correctly identifying
        /// a file that is still being written.
        /// <see cref="UnauthorizedAccessException"/> is treated as complete: the file exists but we lack
        /// permission (e.g. read-only attribute, network share ACL) and is not being actively written to.
        /// </summary>
        internal static bool IsFileWriteComplete(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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

        /// <summary>
        /// Walks the library folders and builds a fingerprint index so <see cref="IsAlreadyInLibrary"/> can detect
        /// files that were previously imported (regardless of the name they were imported under).
        /// Uses a persisted cache so only new or modified library files are fingerprinted; deleted files are pruned.
        /// If called concurrently (e.g. from both the sync loop and a UI scan), the second caller waits for the
        /// first to finish. When <paramref name="allowReuse"/> is true, the existing index is reused for up to
        /// <see cref="FullRebuildIntervalCycles"/> cycles before forcing a full rebuild to catch external changes.
        /// </summary>
        internal static void BuildLibraryIndex(string moviesLibraryPath, string tvShowsLibraryPath, bool allowReuse = false, CancellationToken cancellationToken = default)
        {
            _libraryBuildSemaphore.Wait(cancellationToken);
            try
            {
                bool forceRebuild = _libraryBuildCycleCount >= FullRebuildIntervalCycles;
                if (!forceRebuild && allowReuse && _libraryFingerprints is not null)
                {
                    LogManager.Instance.LogDebug($"MediaImporter.BuildLibraryIndex: Reusing index (cycle {_libraryBuildCycleCount}/{FullRebuildIntervalCycles})", Subsystem.MediaManager);
                    _libraryBuildCycleCount++;
                    return;
                }
                if (forceRebuild)
                    LogManager.Instance.LogDebug($"MediaImporter.BuildLibraryIndex: Forcing periodic rebuild (every {FullRebuildIntervalCycles} cycles)", Subsystem.MediaManager);
                _libraryBuildCycleCount = 1;

                var libraryPaths = new[] { moviesLibraryPath, tvShowsLibraryPath }
                    .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                    .ToArray();

                LogManager.Instance.LogMessage($"Building library index across {libraryPaths.Length} folder(s)", LogLevel.Info, Subsystem.MediaManager);
                LoadLibraryCache();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var fingerprints = new HashSet<string>(StringComparer.Ordinal);
                var seenPaths    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int cached = 0, computed = 0;

                foreach (var path in libraryPaths)
                {
                    LogManager.Instance.LogMessage($"Enumerating library folder: '{path}'", LogLevel.Info, Subsystem.MediaManager);
                    List<FileInfo> files;
                    try   { files = EnumerateLibraryFolder(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LogManager.Instance.LogMessage($"Skipped library folder '{path}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                        continue;
                    }

                    var (c, comp) = FingerprintLibraryFiles(files, fingerprints, seenPaths, cancellationToken);
                    cached   += c;
                    computed += comp;
                }

                PruneLibraryCache(seenPaths);

                sw.Stop();
                _libraryFingerprints = fingerprints;
                LogManager.Instance.LogMessage(
                    $"Library index built: {fingerprints.Count} files in {sw.ElapsedMilliseconds}ms (cached={cached}, computed={computed})",
                    LogLevel.Info, Subsystem.MediaManager);
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

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var filePath = GetCacheFilePath(LibraryCacheFileName);
                if (!File.Exists(filePath))
                {
                    _libraryCache = new(StringComparer.OrdinalIgnoreCase);
                    sw.Stop();
                    LogManager.Instance.LogMessage($"Library cache loaded: 0 entries in {sw.ElapsedMilliseconds}ms", LogLevel.Info, Subsystem.MediaManager);
                    return;
                }

                var json = File.ReadAllText(filePath);
                var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
                _libraryCache = entries is not null
                    ? new Dictionary<string, CacheEntry>(entries, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);
                _libraryCacheDirty = false;

                sw.Stop();
                LogManager.Instance.LogDebug($"MediaImporter.LoadLibraryCache: Loaded {_libraryCache.Count} entries", Subsystem.MediaManager);
                LogManager.Instance.LogMessage($"Library cache loaded: {_libraryCache.Count} entries in {sw.ElapsedMilliseconds}ms", LogLevel.Info, Subsystem.MediaManager);
            }
            catch (Exception ex)
            {
                sw.Stop();
                LogManager.Instance.LogMessage($"Library cache could not be loaded, starting fresh: {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                _libraryCache = new(StringComparer.OrdinalIgnoreCase);
            }
        }

        // Enumerates all files in a library folder.
        // FileInfo metadata (Length, LastWriteTimeUtc) comes from the directory listing - no extra stat per file.
        // IgnoreInaccessible = true silently skips permission-denied folders.
        // No MaxRecursionDepth: Plex libraries are shallow by convention (Title/Season/file), so no cap needed.
        private static List<FileInfo> EnumerateLibraryFolder(string folder) =>
            new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible    = true
            }).ToList();

        // Fingerprints a pre-enumerated list of library files in parallel, using the cache where possible.
        // Each file requires a 128 KB read to compute its fingerprint; reads are bounded by FingerprintParallelism.
        private static (int Cached, int Computed) FingerprintLibraryFiles(
            List<FileInfo> files, HashSet<string> fingerprints, HashSet<string> seenPaths,
            CancellationToken cancellationToken)
        {
            var results = new ConcurrentBag<(string Fingerprint, bool WasCached)>();
            var seen    = new ConcurrentBag<string>();

            Parallel.ForEach(files,
                new ParallelOptions { MaxDegreeOfParallelism = FingerprintParallelism, CancellationToken = cancellationToken },
                fi =>
                {
                    seen.Add(fi.FullName);
                    try
                    {
                        string fp = GetOrComputeLibraryFingerprint(fi, out bool wasCached);
                        results.Add((fp, wasCached));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LogManager.Instance.LogDebug($"MediaImporter.FingerprintLibraryFiles: Skipped '{fi.Name}': {ex.Message}", Subsystem.MediaManager);
                    }
                });

            // Merge into caller-provided collections (single-threaded; no concurrent access from here)
            int cached = 0, computed = 0;
            foreach (var (fp, wasCached) in results)
            {
                fingerprints.Add(fp);
                if (wasCached) cached++; else computed++;
            }
            foreach (var p in seen)
                seenPaths.Add(p);

            return (cached, computed);
        }

        // Returns a cached library fingerprint if metadata matches, otherwise computes and caches it.
        private static string GetOrComputeLibraryFingerprint(FileInfo fi, out bool wasCached)
        {
            var cache = _libraryCache;

            if (cache is not null)
            {
                lock (_libraryLock)
                {
                    if (cache.TryGetValue(fi.FullName, out var entry)
                        && entry.Size == fi.Length
                        && entry.LastWriteTimeTicks == fi.LastWriteTimeUtc.Ticks)
                    {
                        wasCached = true;
                        return entry.Fingerprint;
                    }
                }
            }

            string fp = ComputeFingerprint(fi.FullName);
            if (cache is not null)
            {
                lock (_libraryLock)
                {
                    cache[fi.FullName] = new CacheEntry(fi.Length, fi.LastWriteTimeUtc.Ticks, fp);
                    _libraryCacheDirty = true;
                }
            }
            wasCached = false;
            return fp;
        }

        // Removes library cache entries for files not present in the current directory walk.
        private static void PruneLibraryCache(HashSet<string> seenPaths)
        {
            var cache = _libraryCache!;
            List<string> stale;
            lock (_libraryLock)
            {
                stale = cache.Keys.Where(k => !seenPaths.Contains(k)).ToList();
                if (stale.Count == 0) return;

                foreach (var key in stale)
                    cache.Remove(key);
                _libraryCacheDirty = true;
            }

            LogManager.Instance.LogDebug($"MediaImporter.PruneLibraryCache: Removed {stale.Count} stale entries", Subsystem.MediaManager);
        }

        /// <summary>Adds a file's fingerprint to the library index and cache after a successful import. No-op if the index hasn't been built yet.</summary>
        internal static void AddToLibraryIndex(string importedFilePath)
        {
            var fps = _libraryFingerprints;
            if (fps is null) return;
            try
            {
                var fi = new FileInfo(importedFilePath);
                string fingerprint = ComputeFingerprint(importedFilePath);
                lock (_libraryLock)
                {
                    fps.Add(fingerprint);

                    var cache = _libraryCache;
                    if (cache is not null)
                    {
                        cache[importedFilePath] = new CacheEntry(fi.Length, fi.LastWriteTimeUtc.Ticks, fingerprint);
                        _libraryCacheDirty = true;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
            var fps = _libraryFingerprints;
            if (fps is null) return false;
            try
            {
                string fingerprint = GetOrComputeSourceFingerprint(fi);
                lock (_libraryLock)
                    return fps.Contains(fingerprint);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogManager.Instance.LogDebug($"MediaImporter.IsAlreadyInLibrary: Could not read '{fi.Name}': {ex.Message}", Subsystem.MediaManager);
                return false;
            }
        }

        // Returns the fingerprint for a source file, using the in-memory cache when the file has not changed.
        // Uses _sourceInFlight to deduplicate concurrent computation: if two threads race on the same file,
        // the second waits on the Lazy rather than issuing a redundant read.
        private static string GetOrComputeSourceFingerprint(FileInfo fi)
        {
            var cache = _sourceCache;
            if (cache is not null)
            {
                lock (_sourceCacheLock)
                {
                    if (cache.TryGetValue(fi.FullName, out var entry)
                        && entry.Size == fi.Length
                        && entry.LastWriteTimeTicks == fi.LastWriteTimeUtc.Ticks)
                    {
                        Interlocked.Increment(ref _sourceCachedCount);
                        return entry.Fingerprint;
                    }
                }

                // Create before GetOrAdd so we can tell whether this thread won the race.
                var newLazy = new Lazy<string>(() => ComputeFingerprint(fi.FullName));
                var lazy    = _sourceInFlight.GetOrAdd(fi.FullName, newLazy);
                string fp   = lazy.Value;
                // Remove only our Lazy instance so a newer entry for the same path is left untouched.
                _sourceInFlight.TryRemove(new KeyValuePair<string, Lazy<string>>(fi.FullName, lazy));

                // Winner computed the fingerprint; loser waited on the Lazy and reused the result.
                if (ReferenceEquals(lazy, newLazy))
                    Interlocked.Increment(ref _sourceComputedCount);
                else
                    Interlocked.Increment(ref _sourceCachedCount);

                lock (_sourceCacheLock)
                {
                    cache[fi.FullName] = new CacheEntry(fi.Length, fi.LastWriteTimeUtc.Ticks, fp);
                    _sourceCacheDirty = true;
                }
                return fp;
            }

            Interlocked.Increment(ref _sourceComputedCount);
            return ComputeFingerprint(fi.FullName);
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

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var filePath = GetCacheFilePath(SourceCacheFileName);
                if (!File.Exists(filePath))
                {
                    _sourceCache = new(StringComparer.OrdinalIgnoreCase);
                    sw.Stop();
                    LogManager.Instance.LogMessage($"Source cache loaded: 0 entries in {sw.ElapsedMilliseconds}ms", LogLevel.Info, Subsystem.MediaManager);
                    return;
                }

                var json = File.ReadAllText(filePath);
                var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
                _sourceCache = entries is not null
                    ? new Dictionary<string, CacheEntry>(entries, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);
                _sourceCacheDirty = false;

                sw.Stop();
                LogManager.Instance.LogDebug($"MediaImporter.LoadSourceCache: Loaded {_sourceCache.Count} entries", Subsystem.MediaManager);
                LogManager.Instance.LogMessage($"Source cache loaded: {_sourceCache.Count} entries in {sw.ElapsedMilliseconds}ms", LogLevel.Info, Subsystem.MediaManager);
            }
            catch (Exception ex)
            {
                sw.Stop();
                LogManager.Instance.LogMessage($"Source cache could not be loaded, starting fresh: {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                _sourceCache = new(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Persists the source cache to disk, pruning entries for files that no longer exist.
        /// No-op if the cache has not changed since the last save (or load).
        /// </summary>
        internal static void SaveSourceCache()
        {
            var cache = _sourceCache;
            if (cache is null) return;

            try
            {
                Dictionary<string, CacheEntry> snapshot;
                bool wasDirty;
                lock (_sourceCacheLock)
                {
                    snapshot  = new Dictionary<string, CacheEntry>(cache, StringComparer.OrdinalIgnoreCase);
                    wasDirty  = _sourceCacheDirty;
                    _sourceCacheDirty = false; // reset inside lock so concurrent writes after this point re-set it
                }

                // If every cached entry was visited this cycle, none can be stale - skip File.Exists entirely.
                // If the counts differ, at least one entry was not visited (possibly deleted from source).
                int visited = _sourceCachedCount + _sourceComputedCount;
                bool mightHaveStaleEntries = snapshot.Count > visited;

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
            var cache = _libraryCache;
            if (cache is null) return;

            try
            {
                Dictionary<string, CacheEntry> snapshot;
                bool wasDirty;
                lock (_libraryLock)
                {
                    snapshot       = new Dictionary<string, CacheEntry>(cache, StringComparer.OrdinalIgnoreCase);
                    wasDirty       = _libraryCacheDirty;
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
                _libraryFingerprints    = null;
                _libraryCache           = null;
                _libraryCacheDirty      = false;
                _libraryBuildCycleCount = 0;
            }

            AppConstants.TryDeleteFile(GetCacheFilePath(SourceCacheFileName));
            AppConstants.TryDeleteFile(GetCacheFilePath(LibraryCacheFileName));

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
            public uint     FileAttributes;
            public FileTime CreationTime;
            public FileTime LastAccessTime;
            public FileTime LastWriteTime;
            public uint     VolumeSerialNumber;
            public uint     FileSizeHigh;
            public uint     FileSizeLow;
            public uint     NumberOfLinks;
            public uint     FileIndexHigh;
            public uint     FileIndexLow;
        }
    }
}
