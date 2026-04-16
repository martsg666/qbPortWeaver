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
        private readonly TmdbClient _tmdb;
        private readonly bool _dryRun;
        private readonly bool _createFolders;
        private readonly string _libraryPath;
        private readonly ImportMode _importMode;

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
        /// Scans pre-classified TV episode files and returns import proposals without modifying any files.
        /// Only items not yet present in the library are included.
        /// </summary>
        public async Task<List<MediaProposal>> ScanTvShowsAsync(string[] tvShowFiles, Action? onItemProcessed = null)
        {
            var proposals = new List<MediaProposal>();

            foreach (var file in tvShowFiles)
            {
                await ScanEpisodeFileAsync(file, proposals).ConfigureAwait(false);
                onItemProcessed?.Invoke();
            }

            return proposals;
        }

        /// <summary>Processes pre-classified TV episode files, importing them into the library with Plex naming conventions. Skips uncertain TMDB matches - use <see cref="ScanTvShowsAsync"/> to preview and review those first.</summary>
        public async Task ProcessTvShowsAsync(string sourceFolder, string[] tvShowFiles)
        {
            foreach (var file in tvShowFiles)
                await ProcessEpisodeFileAsync(sourceFolder, file).ConfigureAwait(false);
        }

        // Scans a single TV episode file and adds proposals for unmatched or new items
        private async Task ScanEpisodeFileAsync(string filePath, List<MediaProposal> proposals)
        {
            var fileName = Path.GetFileName(filePath);

            var episodeInfo = FileNameParser.ParseTvShowEpisode(fileName);
            if (episodeInfo is null)
            {
                LogManager.Instance.LogDebug($"TvShowProcessor.ScanEpisodeFileAsync: Skipped '{fileName}' - not a recognized episode", Subsystem.MediaManager);
                return;
            }

            var (info, isConfident) = await GetOrLookupTvShowAsync(episodeInfo.ShowName, episodeInfo.Year).ConfigureAwait(false);
            if (info is null)
            {
                proposals.Add(new MediaProposal(MediaProposal.TypeTvShow, filePath, string.Empty, IsConfident: false, IsMatched: false));
                return;
            }

            var proposedPath = BuildEpisodePath(filePath, info, episodeInfo, _libraryPath, _createFolders);

            if (MediaImporter.IsDuplicateFile(filePath, proposedPath)) return;

            if (!string.Equals(filePath, proposedPath, StringComparison.OrdinalIgnoreCase))
                proposals.Add(new MediaProposal(MediaProposal.TypeTvShow, filePath, proposedPath, isConfident));
        }

        // Imports a single TV episode file into the library
        private async Task ProcessEpisodeFileAsync(string sourceFolder, string filePath)
        {
            var fileName = Path.GetFileName(filePath);

            var episodeInfo = FileNameParser.ParseTvShowEpisode(fileName);
            if (episodeInfo is null)
            {
                LogManager.Instance.LogDebug($"TvShowProcessor.ProcessEpisodeFileAsync: Skipped '{fileName}' - not a recognized episode", Subsystem.MediaManager);
                return;
            }

            var (info, isConfident) = await GetOrLookupTvShowAsync(episodeInfo.ShowName, episodeInfo.Year).ConfigureAwait(false);

            if (info is null) return;
            if (!isConfident)
            {
                LogManager.Instance.LogMessage($"Skipped '{fileName}' - uncertain TMDB match, review in Media Manager", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            var targetPath = BuildEpisodePath(filePath, info, episodeInfo, _libraryPath, _createFolders);
            MediaManagerService.ImportFile(filePath, targetPath, sourceFolder, _dryRun, _importMode);

            MediaManagerService.ImportCompanionFiles(sourceFolder, filePath, targetPath, _dryRun, _importMode);
        }

        // Builds the library target path for an episode file.
        // Multi-episode files (e.g. S01E01E02) are named with both episode numbers: "Show (Year) - S01E01E02.ext"
        // Exposed for MediaManagerForm rematch logic to share the same naming convention.
        internal static string BuildEpisodePath(string filePath, TvShowInfo info, TvShowEpisodeInfo episodeInfo, string libraryPath, bool createFolders)
        {
            var ext             = Path.GetExtension(filePath);
            var plexName        = FileNameParser.FormatPlexName(info.Title, info.Year);
            var episodeCode     = episodeInfo.EndEpisode.HasValue
                ? $"S{episodeInfo.Season:D2}E{episodeInfo.Episode:D2}E{episodeInfo.EndEpisode.Value:D2}"
                : $"S{episodeInfo.Season:D2}E{episodeInfo.Episode:D2}";
            var episodeFileName = $"{plexName} - {episodeCode}{ext}";

            return createFolders
                ? Path.Combine(libraryPath, plexName, $"Season {episodeInfo.Season:D2}", episodeFileName)
                : Path.Combine(libraryPath, episodeFileName);
        }

        // Returns a cached TV show lookup or performs a new TMDB search and caches the result
        private async Task<(TvShowInfo? Info, bool IsConfident)> GetOrLookupTvShowAsync(string title, int? year)
        {
            var cacheKey = $"{title}|{year}";
            if (TmdbCacheManager.TryGetTvShow(cacheKey, out var cached))
                return cached;

            var result = await LookupTvShowAsync(title, year).ConfigureAwait(false);
            TmdbCacheManager.TryAddTvShow(cacheKey, result);
            return result;
        }

        private async Task<(TvShowInfo? Info, bool IsConfident)> LookupTvShowAsync(string title, int? year)
        {
            try
            {
                var (info, isConfident) = await TmdbClient.SearchWithConfidenceAsync(
                    title, year, _tmdb.SearchTvShowAsync, i => i.Year is not null, i => i.Title, i => i.VoteCount).ConfigureAwait(false);

                if (info is null)
                {
                    LogManager.Instance.LogMessage($"No TMDB match found for TV show '{title}'", LogLevel.Warn, Subsystem.MediaManager);
                    return (null, false);
                }

                LogManager.Instance.LogDebug($"TvShowProcessor.LookupTvShowAsync: Matched '{info.Title}' ({info.Year}) [tmdb-{info.TmdbId}]", Subsystem.MediaManager);
                return (info, isConfident);
            }
            catch (HttpRequestException ex)
            {
                LogManager.Instance.LogMessage($"Failed to look up TMDB TV show: {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                return (null, false);
            }
        }
    }
}
