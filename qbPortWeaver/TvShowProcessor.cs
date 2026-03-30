namespace qbPortWeaver
{
    /// <summary>
    /// Processes TV episode files, applying Plex naming conventions and importing them into the library:
    /// Library/Show Name (Year)/Season XX/Show Name (Year) - SXXEXX.ext  - with folder creation
    /// Library/Show Name (Year) - SXXEXX.ext                              - without folder creation
    /// Files are transferred via hardlink, copy, or move depending on the configured import mode.
    /// </summary>
    public sealed class TvShowProcessor
    {
        private const string MediaTypeTvShow            = "TV Show";
        private const int    MinVoteCountForNoYearMatch = 50;

        private readonly TmdbClient _tmdb;
        private readonly bool _dryRun;
        private readonly bool _createFolders;
        private readonly string _libraryPath;
        private readonly ImportMode _importMode;

        // Caches show lookups (including confidence) to avoid redundant TMDB API calls across scan cycles.
        // Key includes year to distinguish same-titled shows (e.g. "Show|1978" vs "Show|2003").
        // ConcurrentDictionary: sync cycle and UI scan can overlap.
        // Intentionally never cleared: the process lifetime is short (tray app session) and TMDB
        // metadata does not change meaningfully within a session. Null results are also cached to
        // avoid hammering the API for titles that consistently return no match.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (TvShowInfo? Info, bool IsConfident)> _showCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Creates a TV show processor that imports episodes into the specified library folder.</summary>
        /// <param name="tmdb">TMDB client for TV show metadata lookups.</param>
        /// <param name="dryRun">When true, logs what would happen without importing any files.</param>
        /// <param name="createFolders">When true, imports files into Plex-recommended season subfolders.</param>
        /// <param name="libraryPath">Target library folder for imported TV shows.</param>
        /// <param name="importMode">Determines how files are transferred: hardlink, copy, or move.</param>
        public TvShowProcessor(TmdbClient tmdb, bool dryRun, bool createFolders, string libraryPath, ImportMode importMode = ImportMode.Hardlink)
        {
            _tmdb          = tmdb;
            _dryRun        = dryRun;
            _createFolders = createFolders;
            _libraryPath   = libraryPath;
            _importMode    = importMode;
        }

        /// <summary>
        /// Scans pre-classified TV episode files and directories and returns import proposals without modifying any files.
        /// Only items not yet present in the library are included.
        /// </summary>
        public async Task<List<MediaProposal>> ScanTvShowsAsync(string[] tvFiles, string[] tvDirs, Action? onItemProcessed = null)
        {
            var proposals = new List<MediaProposal>();

            EvictNullShowCache();

            foreach (var file in tvFiles)
            {
                if (!FileImporter.IsAlreadyInLibrary(file))
                    await ScanEpisodeFileAsync(file, proposals).ConfigureAwait(false);
                onItemProcessed?.Invoke();
            }

            foreach (var dir in tvDirs)
            {
                try
                {
                    await ScanTvShowFolderAsync(dir, proposals).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped TV folder '{Path.GetFileName(dir)}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                }
                onItemProcessed?.Invoke();
            }

            return proposals;
        }

        /// <summary>Processes pre-classified TV episode files and directories, importing them into the library with Plex naming conventions. Skips uncertain TMDB matches - use <see cref="ScanTvShowsAsync"/> to preview and review those first.</summary>
        public async Task ProcessTvShowsAsync(string sourceFolder, string[] tvFiles, string[] tvDirs, Action? onItemProcessed = null)
        {
            EvictNullShowCache();

            foreach (var file in tvFiles)
            {
                if (!FileImporter.IsAlreadyInLibrary(file))
                    await ProcessEpisodeFileAsync(sourceFolder, file).ConfigureAwait(false);
                onItemProcessed?.Invoke();
            }

            foreach (var dir in tvDirs)
            {
                try
                {
                    await ProcessTvShowFolderAsync(sourceFolder, dir).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped TV folder '{Path.GetFileName(dir)}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                }
                onItemProcessed?.Invoke();
            }
        }

        private async Task ScanEpisodeFileAsync(string filePath, List<MediaProposal> proposals)
        {
            var fileName = Path.GetFileName(filePath);

            var episodeInfo = FileNameParser.ParseTvShowEpisode(fileName);
            if (episodeInfo is null)
            {
                LogManager.Instance.LogDebug($"TvShowProcessor.ScanEpisodeFileAsync: Skipped '{fileName}' - not a recognised episode", Subsystem.MediaManager);
                return;
            }

            var (showInfo, isConfident) = await GetOrLookupShowAsync(episodeInfo.ShowName, episodeInfo.Year).ConfigureAwait(false);
            if (showInfo is null)
            {
                proposals.Add(new MediaProposal(MediaTypeTvShow, filePath, string.Empty, IsConfident: false, IsMatched: false));
                return;
            }

            var proposedPath = BuildEpisodePath(filePath, showInfo, episodeInfo);

            if (FileImporter.IsDuplicateFile(filePath, proposedPath)) return;

            if (!string.Equals(filePath, proposedPath, StringComparison.OrdinalIgnoreCase))
                proposals.Add(new MediaProposal(MediaTypeTvShow, filePath, proposedPath, isConfident));
        }

        private async Task ScanTvShowFolderAsync(string dirPath, List<MediaProposal> proposals, int depth = 0)
        {
            var files = MediaManagerService.GetFolderFiles(dirPath, depth, MediaManagerService.MaxSubfolderDepth, "TV");
            if (files is null) return;

            var episodeFiles = files
                .Where(f => FileNameParser.IsVideoTvShowEpisode(f) && !FileImporter.IsAlreadyInLibrary(f))
                .ToList();

            if (episodeFiles.Count > 0)
            {
                foreach (var file in episodeFiles)
                    await ScanEpisodeFileAsync(file, proposals).ConfigureAwait(false);
            }

            foreach (var subDir in Directory.GetDirectories(dirPath))
                await ScanTvShowFolderAsync(subDir, proposals, depth + 1).ConfigureAwait(false);
        }

        private async Task ProcessEpisodeFileAsync(string sourceFolder, string filePath)
        {
            var fileName = Path.GetFileName(filePath);

            var episodeInfo = FileNameParser.ParseTvShowEpisode(fileName);

            if (episodeInfo is null)
            {
                LogManager.Instance.LogDebug($"TvShowProcessor.ProcessEpisodeFileAsync: Skipped '{fileName}' - not a recognised episode", Subsystem.MediaManager);
                return;
            }

            var (showInfo, isConfident) = await GetOrLookupShowAsync(episodeInfo.ShowName, episodeInfo.Year).ConfigureAwait(false);

            if (showInfo is null) return;
            if (!isConfident)
            {
                LogManager.Instance.LogMessage($"Skipped '{fileName}' - uncertain TMDB match, review in Media Manager", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            var targetPath = BuildEpisodePath(filePath, showInfo, episodeInfo);
            MediaManagerService.ImportFileWithLog(filePath, targetPath, sourceFolder, _dryRun, _importMode);

            ImportCompanionFiles(sourceFolder, filePath, targetPath, _dryRun, _importMode);
        }

        private async Task ProcessTvShowFolderAsync(string sourceFolder, string dirPath, int depth = 0)
        {
            var files = MediaManagerService.GetFolderFiles(dirPath, depth, MediaManagerService.MaxSubfolderDepth, "TV");
            if (files is null) return;

            var episodeFiles = files
                .Where(f => FileNameParser.IsVideoTvShowEpisode(f) && !FileImporter.IsAlreadyInLibrary(f))
                .ToList();

            if (episodeFiles.Count > 0)
            {
                foreach (var file in episodeFiles)
                    await ProcessEpisodeFileAsync(sourceFolder, file).ConfigureAwait(false);
            }

            foreach (var subDir in Directory.GetDirectories(dirPath))
                await ProcessTvShowFolderAsync(sourceFolder, subDir, depth + 1).ConfigureAwait(false);
        }

        // Builds the library target path for an episode file
        private string BuildEpisodePath(string filePath, TvShowInfo showInfo, TvShowEpisodeInfo episodeInfo)
        {
            var ext             = Path.GetExtension(filePath);
            var showFolderName  = FileNameParser.FormatPlexName(showInfo.Title, showInfo.Year);
            var episodeFileName = $"{showFolderName} - S{episodeInfo.Season:D2}E{episodeInfo.Episode:D2}{ext}";

            return _createFolders
                ? Path.Combine(_libraryPath, showFolderName, $"Season {episodeInfo.Season:D2}", episodeFileName)
                : Path.Combine(_libraryPath, episodeFileName);
        }

        private static void ImportCompanionFiles(string sourceFolder, string videoPath, string targetVideoPath, bool dryRun, ImportMode importMode) =>
            MediaManagerService.ImportCompanionFiles(sourceFolder, videoPath, targetVideoPath, dryRun, importMode);

        // Evicts cached null results so transient API failures are retried each scan.
        // Within a scan, null results are still cached to avoid duplicate lookups.
        private static void EvictNullShowCache()
        {
            foreach (var key in _showCache.Keys.ToList())
                if (_showCache.TryGetValue(key, out var cached) && cached.Info is null)
                    _showCache.TryRemove(key, out _);
        }

        // Returns a cached show lookup or performs a new TMDB search and caches the result
        private async Task<(TvShowInfo? Info, bool IsConfident)> GetOrLookupShowAsync(string showName, int? year)
        {
            var cacheKey = $"{showName}|{year}";
            if (_showCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var result = await LookupTvShowAsync(showName, year).ConfigureAwait(false);
            _showCache.TryAdd(cacheKey, result);
            return result;
        }

        private async Task<(TvShowInfo? Info, bool IsConfident)> LookupTvShowAsync(string title, int? year)
        {
            try
            {
                bool isConfident = true;

                var info = await _tmdb.SearchTvShowAsync(title, year).ConfigureAwait(false);

                // Without a year in the filename we cannot corroborate the match by year alone.
                // Require an exact title match and a meaningful vote count to stay confident.
                if (info is not null && !year.HasValue)
                    isConfident = FileNameParser.IsStrongNoYearMatch(title, info.Title, info.VoteCount, MinVoteCountForNoYearMatch);

                // Retry without year: parsed year may be the season year rather than TMDB's first-air year
                if (info is null && year.HasValue)
                {
                    info = await _tmdb.SearchTvShowAsync(title).ConfigureAwait(false);
                    if (info is not null) isConfident = false;
                }

                (info, isConfident) = await TryFallbackLookupsAsync(title, year, info, isConfident).ConfigureAwait(false);

                if (info is null)
                {
                    LogManager.Instance.LogMessage($"No TMDB match found for show '{title}'", LogLevel.Warn, Subsystem.MediaManager);
                    return (null, false);
                }

                LogManager.Instance.LogDebug($"TvShowProcessor.LookupTvShowAsync: Matched '{info.Title}' ({info.Year}) [tmdb-{info.TmdbId}]", Subsystem.MediaManager);
                return (info, isConfident);
            }
            catch (HttpRequestException ex)
            {
                LogManager.Instance.LogMessage($"Failed to look up TMDB TV show: {ex.Message}", LogLevel.Error, Subsystem.MediaManager);
                return (null, false);
            }
        }

        // Applies two fallback TMDB lookup strategies to reduce unmatched results.
        // After-dash strategy: only runs when info is null (no match from initial lookups).
        // Trailing-number strategy: runs when info is null OR info.Year is null.
        private async Task<(TvShowInfo? Info, bool IsConfident)> TryFallbackLookupsAsync(
            string title, int? year, TvShowInfo? info, bool isConfident)
        {
            // Try the part after " - " (e.g. "Series 1 - The Subtitle")
            var afterDash = info is null ? FileNameParser.ExtractAfterDash(title) : null;

            if (afterDash is not null)
            {
                LogManager.Instance.LogDebug($"TvShowProcessor.TryFallbackLookupsAsync: Retrying with '{afterDash}'", Subsystem.MediaManager);
                info = await _tmdb.SearchTvShowAsync(afterDash, year).ConfigureAwait(false);
                if (info is not null) isConfident = false;
            }

            // Try without trailing number (e.g. "Title 1" -> "Title")
            // Also runs when info is not null but lacks a year: a year-less TMDB result is low-quality
            // (ambiguous match), so we attempt a stripped title in case TMDB returns a better-quality
            // result that includes a year. If found, it replaces the existing match with isConfident=false
            // so the user can review it in the Media Manager before the file is imported.
            if (info is null || (info.Year is null && title.Length > 2))
            {
                var altInfo = await TryWithoutTrailingNumberAsync(title, year).ConfigureAwait(false);
                if (altInfo is not null)
                {
                    info        = altInfo;
                    isConfident = false;
                }
            }

            return (info, isConfident);
        }

        // Strips a single trailing digit preceded by a space (e.g. "Title 2" -> "Title")
        // and searches TMDB. Returns the match only if it includes a year (quality gate).
        private async Task<TvShowInfo?> TryWithoutTrailingNumberAsync(string title, int? year)
        {
            var withoutNum = FileNameParser.StripTrailingNumber(title);
            if (withoutNum is null) return null;

            LogManager.Instance.LogDebug($"TvShowProcessor.TryWithoutTrailingNumberAsync: Retrying without trailing number '{withoutNum}'", Subsystem.MediaManager);
            var altInfo = await _tmdb.SearchTvShowAsync(withoutNum, year).ConfigureAwait(false);
            return altInfo?.Year is not null ? altInfo : null;
        }
    }
}
