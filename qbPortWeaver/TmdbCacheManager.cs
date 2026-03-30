using System.Collections.Concurrent;
using System.Text.Json;

namespace qbPortWeaver
{
    /// <summary>
    /// Manages TMDB lookup caches for both movies and TV shows, with disk persistence across sessions.
    /// Results are stored with a timestamp and discarded after <see cref="TtlDays"/> days.
    /// Only successful (non-null) results are persisted; null results are session-only to allow retries.
    /// </summary>
    internal static class TmdbCacheManager
    {
        private const int    TtlDays            = 30;
        private const string ShowCacheFileName  = "qbPortWeaver.tmdb.shows.json";
        private const string MovieCacheFileName = "qbPortWeaver.tmdb.movies.json";

        // ConcurrentDictionary: sync cycle and UI scan can overlap.
        private static readonly ConcurrentDictionary<string, (TvShowInfo? Info, bool IsConfident)>
            _showCache  = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, (MovieInfo? Info, bool IsConfident)>
            _movieCache = new(StringComparer.OrdinalIgnoreCase);

        private static int _loaded; // 0 = not loaded, 1 = loaded; Interlocked
        private static volatile bool _showCacheDirty;
        private static volatile bool _movieCacheDirty;

        // Serialised on-disk format: only non-null results are persisted.
        private sealed record TmdbEntry<T>(T? Info, bool IsConfident, DateTime CachedAt) where T : class;

        internal static bool TryGetShow(string key, out (TvShowInfo? Info, bool IsConfident) result)
            => _showCache.TryGetValue(key, out result);

        internal static void TryAddShow(string key, (TvShowInfo? Info, bool IsConfident) value)
        {
            if (_showCache.TryAdd(key, value) && value.Info is not null)
                _showCacheDirty = true;
        }

        internal static bool TryGetMovie(string key, out (MovieInfo? Info, bool IsConfident) result)
            => _movieCache.TryGetValue(key, out result);

        internal static void TryAddMovie(string key, (MovieInfo? Info, bool IsConfident) value)
        {
            if (_movieCache.TryAdd(key, value) && value.Info is not null)
                _movieCacheDirty = true;
        }

        /// <summary>Evicts cached null show results so transient API failures are retried next cycle.</summary>
        internal static void EvictNullShows() => EvictNullsFromCache(_showCache);

        /// <summary>Evicts cached null movie results so transient API failures are retried next cycle.</summary>
        internal static void EvictNullMovies() => EvictNullsFromCache(_movieCache);

        /// <summary>
        /// Loads both TMDB caches from disk. No-op if already loaded this session.
        /// Entries older than <see cref="TtlDays"/> days are silently discarded.
        /// </summary>
        internal static void Load()
        {
            if (Interlocked.CompareExchange(ref _loaded, 1, 0) != 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LoadFromDisk(_showCache, ShowCacheFileName, "show");
            LoadFromDisk(_movieCache, MovieCacheFileName, "movie");
            sw.Stop();
            LogManager.Instance.LogMessage(
                $"TMDB cache loaded: {_showCache.Count} show entries, {_movieCache.Count} movie entries in {sw.ElapsedMilliseconds}ms",
                LogLevel.Info, Subsystem.MediaManager);
        }

        /// <summary>Persists both TMDB caches to disk. Each cache is saved independently; no-op for a cache that has not changed.</summary>
        internal static void Save()
        {
            if (!_showCacheDirty && !_movieCacheDirty) return;
            int shows  = 0;
            int movies = 0;
            if (_showCacheDirty)  { shows  = SaveToDisk(_showCache,  ShowCacheFileName,  "show");  _showCacheDirty  = false; }
            if (_movieCacheDirty) { movies = SaveToDisk(_movieCache, MovieCacheFileName, "movie"); _movieCacheDirty = false; }
            LogManager.Instance.LogMessage(
                $"TMDB cache saved: {shows} show entries, {movies} movie entries",
                LogLevel.Info, Subsystem.MediaManager);
        }

        /// <summary>Clears both caches from memory and deletes the on-disk cache files.</summary>
        internal static void Clear()
        {
            _showCache.Clear();
            _movieCache.Clear();
            _showCacheDirty  = false;
            _movieCacheDirty = false;
            Interlocked.Exchange(ref _loaded, 0);
            FileImporter.TryDeleteFile(FileImporter.GetCacheFilePath(ShowCacheFileName));
            FileImporter.TryDeleteFile(FileImporter.GetCacheFilePath(MovieCacheFileName));
            LogManager.Instance.LogMessage("TMDB caches cleared", LogLevel.Info, Subsystem.MediaManager);
        }

        private static void EvictNullsFromCache<T>(ConcurrentDictionary<string, (T? Info, bool IsConfident)> cache) where T : class
        {
            foreach (var key in cache.Keys.ToList())
                if (cache.TryGetValue(key, out var entry) && entry.Info is null)
                    cache.TryRemove(key, out _);
        }

        private static void LoadFromDisk<T>(
            ConcurrentDictionary<string, (T? Info, bool IsConfident)> cache, string fileName, string label) where T : class
        {
            var filePath = FileImporter.GetCacheFilePath(fileName);
            if (!File.Exists(filePath)) return;

            try
            {
                var json    = File.ReadAllText(filePath);
                var entries = JsonSerializer.Deserialize<Dictionary<string, TmdbEntry<T>>>(json);
                if (entries is null) return;

                var cutoff  = DateTime.UtcNow.AddDays(-TtlDays);
                int loaded  = 0;
                int expired = 0;

                foreach (var (key, entry) in entries)
                {
                    if (entry.CachedAt < cutoff || entry.Info is null) { expired++; continue; }
                    cache.TryAdd(key, (entry.Info, entry.IsConfident));
                    loaded++;
                }

                LogManager.Instance.LogDebug(
                    $"TmdbCacheManager.LoadFromDisk: Loaded {loaded} {label} entries ({expired} expired)",
                    Subsystem.MediaManager);
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage(
                    $"TMDB {label} cache could not be loaded, starting fresh: {ex.Message}",
                    LogLevel.Warn, Subsystem.MediaManager);
            }
        }

        private static int SaveToDisk<T>(
            ConcurrentDictionary<string, (T? Info, bool IsConfident)> cache, string fileName, string label) where T : class
        {
            try
            {
                var now    = DateTime.UtcNow;
                var toSave = cache
                    .Where(kv => kv.Value.Info is not null)
                    .ToDictionary(
                        kv => kv.Key,
                        kv => new TmdbEntry<T>(kv.Value.Info, kv.Value.IsConfident, now),
                        StringComparer.OrdinalIgnoreCase);

                var json = JsonSerializer.Serialize(toSave, FileImporter.JsonWriteOptions);
                FileImporter.WriteAtomic(FileImporter.GetCacheFilePath(fileName), json);

                LogManager.Instance.LogDebug(
                    $"TmdbCacheManager.SaveToDisk: Saved {toSave.Count} {label} entries",
                    Subsystem.MediaManager);

                return toSave.Count;
            }
            catch (Exception ex)
            {
                LogManager.Instance.LogMessage(
                    $"Failed to save TMDB {label} cache: {ex.Message}",
                    LogLevel.Warn, Subsystem.MediaManager);
                return 0;
            }
        }
    }
}
