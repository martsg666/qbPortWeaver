using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace qbPortWeaver
{
    /// <summary>Imports media files into the library via hardlink, copy, or move, with automatic hardlink-to-copy fallback.</summary>
    internal static class FileImporter
    {
        // Library index: fingerprints (size + partial SHA-256) of every file in the library folders.
        private static HashSet<string>? _libraryFingerprints;
        private static readonly object _libraryLock = new();

        // Library cache: persisted path -> metadata so unchanged library files are not re-hashed across sessions.
        private static Dictionary<string, CacheEntry>? _libraryCache;
        private static bool _libraryCacheDirty;

        // Source scan cache: maps source file paths to their fingerprint so unchanged files are not re-hashed each cycle.
        private static Dictionary<string, CacheEntry>? _sourceCache;
        private static readonly object _sourceCacheLock = new();
        private static bool _sourceCacheDirty;

        private const int FingerprintChunkBytes = 64 * 1024; // 64 KB per chunk (head + tail)
        private const string SourceCacheFileName  = "qbPortWeaver.mediascan.json";
        private const string LibraryCacheFileName = "qbPortWeaver.medialibrary.json";

        private sealed record CacheEntry(long Size, long LastWriteTimeTicks, string Fingerprint);

        // CreateHardLink: lpFileName is the NEW link (destination), lpExistingFileName is the existing file (source).
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, nint lpSecurityAttributes);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

        /// <summary>Attempts to create a hardlink at <paramref name="destinationPath"/> pointing to <paramref name="sourcePath"/>.
        /// Uses the extended-length path prefix to support paths longer than MAX_PATH (260 characters).</summary>
        /// <returns><see langword="true"/> if the hardlink was created; <see langword="false"/> on failure (e.g. cross-volume, unsupported filesystem).</returns>
        internal static bool TryCreateHardLink(string sourcePath, string destinationPath)
        {
            bool result = CreateHardLink(ToExtendedPath(destinationPath), ToExtendedPath(sourcePath), nint.Zero);
            if (!result)
            {
                int error = Marshal.GetLastWin32Error();
                LogManager.Instance.LogDebug($"FileImporter.TryCreateHardLink: Failed (Win32 error {error}) - '{Path.GetFileName(sourcePath)}'", Subsystem.MediaManager);
            }
            return result;
        }

        // Verifies that two paths refer to the same file by comparing volume serial number and file index.
        // Returns false if the files have different identities (NAS silently created a copy instead of a hardlink).
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
                LogManager.Instance.LogDebug($"FileImporter.VerifyHardLink: Could not verify - {ex.Message}", Subsystem.MediaManager);
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
        /// Imports a file from <paramref name="sourcePath"/> to <paramref name="destinationPath"/> using the specified <paramref name="importMode"/>.
        /// Creates the target directory if needed. Skips files that already exist at the destination with the same size.
        /// In <see cref="ImportMode.Hardlink"/> mode, automatically falls back to copy if the hardlink fails.
        /// </summary>
        internal static void ImportFile(string sourcePath, string destinationPath, ImportMode importMode)
        {
            if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
                return;

            if (IsDuplicateFile(sourcePath, destinationPath))
            {
                LogManager.Instance.LogDebug($"FileImporter.ImportFile: Skipped - target already exists with same size: '{Path.GetFileName(destinationPath)}'", Subsystem.MediaManager);
                return;
            }

            // Destination exists but different size: two different source files resolved to the same target path
            if (File.Exists(destinationPath))
            {
                LogManager.Instance.LogMessage(
                    $"Destination conflict: '{Path.GetFileName(destinationPath)}' already exists with a different size (source: {new FileInfo(sourcePath).Length}, dest: {new FileInfo(destinationPath).Length}). Skipping to avoid overwriting.",
                    LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            var targetDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            switch (importMode)
            {
                case ImportMode.Hardlink:
                    if (TryCreateHardLink(sourcePath, destinationPath))
                    {
                        if (VerifyHardLink(sourcePath, destinationPath))
                        {
                            LogManager.Instance.LogDebug($"FileImporter.ImportFile: Hardlinked '{Path.GetFileName(destinationPath)}'", Subsystem.MediaManager);
                        }
                        else
                        {
                            LogManager.Instance.LogMessage($"Hardlink not verified for '{Path.GetFileName(destinationPath)}' (NAS created a copy instead), replacing with proper copy", LogLevel.Warn, Subsystem.MediaManager);
                            File.Delete(destinationPath);
                            File.Copy(sourcePath, destinationPath, overwrite: false);
                            LogManager.Instance.LogDebug($"FileImporter.ImportFile: Copied (verified fallback) '{Path.GetFileName(destinationPath)}'", Subsystem.MediaManager);
                        }
                    }
                    else
                    {
                        LogManager.Instance.LogMessage("Hardlink failed, falling back to copy", LogLevel.Warn, Subsystem.MediaManager);
                        File.Copy(sourcePath, destinationPath, overwrite: false);
                        LogManager.Instance.LogDebug($"FileImporter.ImportFile: Copied (fallback) '{Path.GetFileName(destinationPath)}'", Subsystem.MediaManager);
                    }
                    break;

                case ImportMode.Copy:
                    File.Copy(sourcePath, destinationPath, overwrite: false);
                    LogManager.Instance.LogDebug($"FileImporter.ImportFile: Copied '{Path.GetFileName(destinationPath)}'", Subsystem.MediaManager);
                    break;

                case ImportMode.Move:
                    File.Move(sourcePath, destinationPath);
                    LogManager.Instance.LogDebug($"FileImporter.ImportFile: Moved '{Path.GetFileName(destinationPath)}'", Subsystem.MediaManager);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(importMode), importMode, "Unsupported import mode");
            }

            AddToLibraryIndex(destinationPath);
        }

        /// <summary>Returns <see langword="true"/> if the destination file already exists and has the same size as the source.</summary>
        internal static bool IsDuplicateFile(string sourcePath, string destinationPath)
        {
            if (!File.Exists(destinationPath))
                return false;

            var sourceInfo = new FileInfo(sourcePath);
            var destInfo   = new FileInfo(destinationPath);
            return sourceInfo.Length == destInfo.Length;
        }

        /// <summary>
        /// Computes a lightweight fingerprint for a file: the file size combined with a SHA-256 hash of the first
        /// and last 64 KB. For files up to 128 KB the entire content is hashed. Reading only 128 KB keeps this fast
        /// even for multi-gigabyte video files on spinning disks or network shares.
        /// </summary>
        internal static string ComputeFingerprint(string path)
        {
            var fi = new FileInfo(path);
            long size = fi.Length;
            if (size == 0) return "0";

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

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
        /// No-ops if the index is already built unless <paramref name="force"/> is true.
        /// Uses a persisted cache so only new or modified library files are fingerprinted; deleted files are pruned.
        /// After the initial build, call <see cref="AddToLibraryIndex"/> after each import to keep it current.
        /// </summary>
        internal static void BuildLibraryIndex(bool force, params string[] libraryPaths)
        {
            if (!force && _libraryFingerprints is not null)
                return;

            LogManager.Instance.LogMessage("Building library index...", LogLevel.Info, Subsystem.MediaManager);
            LoadLibraryCache();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int cached = 0;
            int computed = 0;

            foreach (var path in libraryPaths.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)))
                IndexLibraryPath(path, fingerprints, seenPaths, ref cached, ref computed);

            PruneLibraryCache(seenPaths);

            sw.Stop();
            _libraryFingerprints = fingerprints;
            LogManager.Instance.LogMessage(
                $"Library index built: {fingerprints.Count} files in {sw.ElapsedMilliseconds}ms (cached={cached}, computed={computed})",
                LogLevel.Info, Subsystem.MediaManager);
        }

        // Enumerates a single library path and fingerprints each file, using the cache where possible.
        // EnumerateFiles on DirectoryInfo returns FileInfo objects whose Length and LastWriteTimeUtc
        // are populated from the directory enumeration data -- no extra per-file network round-trip
        // on NAS/SMB shares.
        private static void IndexLibraryPath(
            string path, HashSet<string> fingerprints, HashSet<string> seenPaths,
            ref int cached, ref int computed)
        {
            try
            {
                foreach (var fi in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    seenPaths.Add(fi.FullName);
                    try
                    {
                        string fp = GetOrComputeLibraryFingerprint(fi, out bool wasCached);
                        fingerprints.Add(fp);
                        if (wasCached) cached++; else computed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        LogManager.Instance.LogDebug($"FileImporter.IndexLibraryPath: Skipped '{fi.Name}': {ex.Message}", Subsystem.MediaManager);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogManager.Instance.LogMessage($"Library index: skipped '{path}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
            }
        }

        // Returns a cached library fingerprint if metadata matches, otherwise computes and caches it.
        private static string GetOrComputeLibraryFingerprint(FileInfo fi, out bool wasCached)
        {
            var cache = _libraryCache!;

            if (cache.TryGetValue(fi.FullName, out var entry)
                && entry.Size == fi.Length
                && entry.LastWriteTimeTicks == fi.LastWriteTimeUtc.Ticks)
            {
                wasCached = true;
                return entry.Fingerprint;
            }

            string fp = ComputeFingerprint(fi.FullName);
            cache[fi.FullName] = new CacheEntry(fi.Length, fi.LastWriteTimeUtc.Ticks, fp);
            _libraryCacheDirty = true;
            wasCached = false;
            return fp;
        }

        // Removes library cache entries for files not present in the current directory walk.
        private static void PruneLibraryCache(HashSet<string> seenPaths)
        {
            var cache = _libraryCache!;
            var stale = cache.Keys.Where(k => !seenPaths.Contains(k)).ToList();
            if (stale.Count == 0) return;

            foreach (var key in stale)
                cache.Remove(key);
            _libraryCacheDirty = true;

            LogManager.Instance.LogDebug($"FileImporter.PruneLibraryCache: Removed {stale.Count} stale entries", Subsystem.MediaManager);
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) // NOSONAR S108 S2486 - best-effort cache update; next full rebuild will recover
            {
            }
        }

        /// <summary>Returns <see langword="true"/> if a file with the same fingerprint already exists somewhere in the library.</summary>
        internal static bool IsAlreadyInLibrary(string sourcePath)
        {
            var fps = _libraryFingerprints;
            if (fps is null) return false;
            try
            {
                var fi = new FileInfo(sourcePath);
                string fingerprint = GetOrComputeSourceFingerprint(fi);
                bool found;
                lock (_libraryLock)
                    found = fps.Contains(fingerprint);
                if (!found)
                    LogManager.Instance.LogDebug($"FileImporter.IsAlreadyInLibrary: No match - '{Path.GetFileName(sourcePath)}' fingerprint={fingerprint}", Subsystem.MediaManager);
                return found;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // Returns the fingerprint for a source file, using the in-memory cache when the file has not changed.
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
                        return entry.Fingerprint;
                    }
                }

                string fp = ComputeFingerprint(fi.FullName);
                lock (_sourceCacheLock)
                {
                    cache[fi.FullName] = new CacheEntry(fi.Length, fi.LastWriteTimeUtc.Ticks, fp);
                    _sourceCacheDirty = true;
                }
                return fp;
            }

            return ComputeFingerprint(fi.FullName);
        }

        /// <summary>
        /// Loads the source scan cache from disk. No-op if already loaded.
        /// The cache maps source file paths to their fingerprints so unchanged files are not re-hashed on subsequent cycles.
        /// </summary>
        internal static void LoadSourceCache()
        {
            if (_sourceCache is not null) return;

            try
            {
                var filePath = GetCacheFilePath(SourceCacheFileName);
                if (!File.Exists(filePath))
                {
                    _sourceCache = new(StringComparer.OrdinalIgnoreCase);
                    return;
                }

                var json = File.ReadAllText(filePath);
                var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
                _sourceCache = entries is not null
                    ? new Dictionary<string, CacheEntry>(entries, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);
                _sourceCacheDirty = false;

                LogManager.Instance.LogDebug($"FileImporter.LoadSourceCache: Loaded {_sourceCache.Count} entries", Subsystem.MediaManager);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Source scan cache could not be loaded, starting fresh: {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                _sourceCache = new(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Persists the source scan cache to disk, pruning entries for files that no longer exist.
        /// No-op if the cache has not changed since the last save (or load).
        /// </summary>
        internal static void SaveSourceCache()
        {
            var cache = _sourceCache;
            if (cache is null || !_sourceCacheDirty) return;

            try
            {
                Dictionary<string, CacheEntry> snapshot;
                lock (_sourceCacheLock)
                    snapshot = new Dictionary<string, CacheEntry>(cache, StringComparer.OrdinalIgnoreCase);

                var toSave = snapshot
                    .Where(kv => File.Exists(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                lock (_sourceCacheLock)
                    _sourceCache = toSave;

                var json = JsonSerializer.Serialize(toSave, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(GetCacheFilePath(SourceCacheFileName), json);
                _sourceCacheDirty = false;

                LogManager.Instance.LogDebug($"FileImporter.SaveSourceCache: Saved {toSave.Count} entries", Subsystem.MediaManager);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to save source scan cache: {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
            }
        }

        // Loads the library fingerprint cache from disk. Initialises an empty cache on first run or if the file is corrupt.
        private static void LoadLibraryCache()
        {
            if (_libraryCache is not null) return;

            try
            {
                var filePath = GetCacheFilePath(LibraryCacheFileName);
                if (!File.Exists(filePath))
                {
                    _libraryCache = new(StringComparer.OrdinalIgnoreCase);
                    return;
                }

                var json = File.ReadAllText(filePath);
                var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
                _libraryCache = entries is not null
                    ? new Dictionary<string, CacheEntry>(entries, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);
                _libraryCacheDirty = false;

                LogManager.Instance.LogDebug($"FileImporter.LoadLibraryCache: Loaded {_libraryCache.Count} entries", Subsystem.MediaManager);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Library cache could not be loaded, starting fresh: {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                _libraryCache = new(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Persists the library fingerprint cache to disk.
        /// No-op if the cache has not changed since the last save (or load).
        /// </summary>
        internal static void SaveLibraryCache()
        {
            var cache = _libraryCache;
            if (cache is null || !_libraryCacheDirty) return;

            try
            {
                string json;
                lock (_libraryLock)
                {
                    json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
                    _libraryCacheDirty = false;
                }
                File.WriteAllText(GetCacheFilePath(LibraryCacheFileName), json);

                LogManager.Instance.LogDebug($"FileImporter.SaveLibraryCache: Saved {cache.Count} entries", Subsystem.MediaManager);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage($"Failed to save library cache: {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
            }
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

            lock (_libraryLock)
            {
                _libraryFingerprints = null;
                _libraryCache = null;
                _libraryCacheDirty = false;
            }

            TryDeleteFile(GetCacheFilePath(SourceCacheFileName));
            TryDeleteFile(GetCacheFilePath(LibraryCacheFileName));

            LogManager.Instance.LogMessage("Fingerprint caches cleared", LogLevel.Info, Subsystem.MediaManager);
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogManager.Instance.LogDebug($"FileImporter.TryDeleteFile: Could not delete '{path}': {ex.Message}", Subsystem.MediaManager);
            }
        }

        private static string GetCacheFilePath(string fileName) =>
            Path.Combine(Path.GetDirectoryName(AppConstants.GetLogFilePath())!, fileName);
    }
}
