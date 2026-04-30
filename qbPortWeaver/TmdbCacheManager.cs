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
        private const int    TtlDays             = 30;
        private const string TvShowCacheFileName = "qbPortWeaver.tmdb.tvshows.json";
        private const string MovieCacheFileName  = "qbPortWeaver.tmdb.movies.json";

        private static readonly JsonSerializerOptions _jsonWriteOptions = new() { WriteIndented = true };

        // ConcurrentDictionary: sync cycle and UI scan can overlap.
        private static readonly ConcurrentDictionary<string, (TvShowInfo? Info, bool IsConfident)>
            _tvShowCache  = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, (MovieInfo? Info, bool IsConfident)>
            _movieCache = new(StringComparer.OrdinalIgnoreCase);

        // In-flight dedup: if two source folders race on the same title, the second awaits
        // the first lookup's Task rather than issuing a second TMDB API call.
        // Lazy<Task> ensures the factory is invoked exactly once even under concurrent GetOrAdd.
        private static readonly ConcurrentDictionary<string, Lazy<Task<(TvShowInfo? Info, bool IsConfident)>>>
            _tvShowInFlight = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Lazy<Task<(MovieInfo? Info, bool IsConfident)>>>
            _movieInFlight  = new(StringComparer.OrdinalIgnoreCase);

        private static int _loaded; // 0 = not loaded, 1 = loaded; Interlocked
        private static volatile bool _tvShowCacheDirty;
        private static volatile bool _movieCacheDirty;

        // Serialized on-disk format: only non-null results are persisted.
        private sealed record TmdbEntry<T>(T? Info, bool IsConfident, DateTime CachedAt) where T : class;

        internal static bool TryGetTvShow(string key, out (TvShowInfo? Info, bool IsConfident) result)
            => _tvShowCache.TryGetValue(key, out result);

        internal static void TryAddTvShow(string key, (TvShowInfo? Info, bool IsConfident) value)
        {
            // Always cache in memory (including null results) to avoid re-hitting the API within the same session.
            // Only mark dirty for non-null results - nulls are not persisted so they are retried on next start.
            if (_tvShowCache.TryAdd(key, value) && value.Info is not null)
                _tvShowCacheDirty = true;
        }

        internal static bool TryGetMovie(string key, out (MovieInfo? Info, bool IsConfident) result)
            => _movieCache.TryGetValue(key, out result);

        internal static void TryAddMovie(string key, (MovieInfo? Info, bool IsConfident) value)
        {
            // Always cache in memory (including null results) to avoid re-hitting the API within the same session.
            // Only mark dirty for non-null results - nulls are not persisted so they are retried on next start.
            if (_movieCache.TryAdd(key, value) && value.Info is not null)
                _movieCacheDirty = true;
        }

        /// <summary>
        /// Returns the cached TV show result for <paramref name="cacheKey"/> if present; otherwise runs
        /// <paramref name="compute"/> exactly once even when concurrent callers race on the same key.
        /// Parallel source-folder scans sharing the same show share one TMDB API call.
        /// </summary>
        internal static async Task<(TvShowInfo? Info, bool IsConfident)> GetOrComputeTvShowAsync(
            string cacheKey, Func<Task<(TvShowInfo? Info, bool IsConfident)>> compute)
        {
            if (TryGetTvShow(cacheKey, out var cached)) return cached;
            // The first caller's CT is captured inside the compute closure; subsequent waiters share that task.
            var lazy = _tvShowInFlight.GetOrAdd(cacheKey, _ => new Lazy<Task<(TvShowInfo? Info, bool IsConfident)>>(compute));
            try
            {
                var result = await lazy.Value.ConfigureAwait(false);
                TryAddTvShow(cacheKey, result);
                return result;
            }
            finally
            {
                _tvShowInFlight.TryRemove(new KeyValuePair<string, Lazy<Task<(TvShowInfo? Info, bool IsConfident)>>>(cacheKey, lazy));
            }
        }

        /// <summary>
        /// Returns the cached movie result for <paramref name="cacheKey"/> if present; otherwise runs
        /// <paramref name="compute"/> exactly once even when concurrent callers race on the same key.
        /// Parallel source-folder scans sharing the same title share one TMDB API call.
        /// </summary>
        internal static async Task<(MovieInfo? Info, bool IsConfident)> GetOrComputeMovieAsync(
            string cacheKey, Func<Task<(MovieInfo? Info, bool IsConfident)>> compute)
        {
            if (TryGetMovie(cacheKey, out var cached)) return cached;
            // The first caller's CT is captured inside the compute closure; subsequent waiters share that task.
            var lazy = _movieInFlight.GetOrAdd(cacheKey, _ => new Lazy<Task<(MovieInfo? Info, bool IsConfident)>>(compute));
            try
            {
                var result = await lazy.Value.ConfigureAwait(false);
                TryAddMovie(cacheKey, result);
                return result;
            }
            finally
            {
                _movieInFlight.TryRemove(new KeyValuePair<string, Lazy<Task<(MovieInfo? Info, bool IsConfident)>>>(cacheKey, lazy));
            }
        }

        /// <summary>Evicts cached null TV show results so transient API failures are retried next cycle.</summary>
        internal static void EvictNullTvShows() => EvictNullsFromCache(_tvShowCache);

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
            LoadFromDisk(_tvShowCache, TvShowCacheFileName, "TV show");
            LoadFromDisk(_movieCache, MovieCacheFileName, "movie");
            sw.Stop();
            LogManager.Instance.LogMessage(
                $"TMDB cache loaded: {_tvShowCache.Count} TV show entries, {_movieCache.Count} movie entries in {sw.ElapsedMilliseconds}ms",
                LogLevel.Info, Subsystem.MediaManager);
        }

        /// <summary>Persists both TMDB caches to disk. Each cache is saved independently; no-op for a cache that has not changed.</summary>
        internal static void Save()
        {
            if (!_tvShowCacheDirty && !_movieCacheDirty) return;
            var parts = new List<string>(2);
            if (_tvShowCacheDirty)
            {
                int n = SaveToDisk(_tvShowCache, TvShowCacheFileName, "TV show");
                if (n >= 0) { parts.Add($"{n} TV show entries"); _tvShowCacheDirty = false; }
            }
            if (_movieCacheDirty)
            {
                int n = SaveToDisk(_movieCache, MovieCacheFileName, "movie");
                if (n >= 0) { parts.Add($"{n} movie entries"); _movieCacheDirty = false; }
            }
            if (parts.Count > 0)
                LogManager.Instance.LogMessage($"TMDB cache saved: {string.Join(", ", parts)}", LogLevel.Info, Subsystem.MediaManager);
        }

        /// <summary>Clears both caches from memory and deletes the on-disk cache files.</summary>
        internal static void Clear()
        {
            _tvShowCache.Clear();
            _movieCache.Clear();
            _tvShowInFlight.Clear();
            _movieInFlight.Clear();
            _tvShowCacheDirty = false;
            _movieCacheDirty  = false;
            Interlocked.Exchange(ref _loaded, 0);
            AppConstants.TryDeleteFile(AppConstants.GetDataFilePath(TvShowCacheFileName));
            AppConstants.TryDeleteFile(AppConstants.GetDataFilePath(MovieCacheFileName));
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
            var filePath = AppConstants.GetDataFilePath(fileName);
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

                var json = JsonSerializer.Serialize(toSave, _jsonWriteOptions);
                AppConstants.WriteAtomic(AppConstants.GetDataFilePath(fileName), json);

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
                return -1; // signal failure so the caller does not clear the dirty flag
            }
        }
    }
}
