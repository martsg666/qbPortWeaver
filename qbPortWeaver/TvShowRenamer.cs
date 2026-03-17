namespace qbPortWeaver
{
    /// <summary>
    /// Renames TV episode files to Plex naming convention:
    /// TV Shows/Show Name (Year)/Season XX/Show Name (Year) - SXXEXX.ext  -with folder creation
    /// TV Shows/Show Name (Year) - SXXEXX.ext                              -without folder creation
    /// </summary>
    public sealed class TvShowRenamer
    {
        private const string MediaTypeTvShow  = "TV";

        private const int    MaxSubfolderDepth = 10; // TV Shows/Show (Year)/Season XX = depth 2; 10 is a safe ceiling

        private readonly TmdbClient _tmdb;
        private readonly bool _dryRun;
        private readonly bool _createFolders;

        // Caches show lookups (including confidence) to avoid redundant TMDB API calls within one scan cycle
        private readonly Dictionary<string, (TvShowInfo Info, bool IsConfident)> _showCache = new(StringComparer.OrdinalIgnoreCase);

        public TvShowRenamer(TmdbClient tmdb, bool dryRun, bool createFolders)
        {
            _tmdb          = tmdb;
            _dryRun        = dryRun;
            _createFolders = createFolders;
        }

        /// <summary>
        /// Scans a TV shows folder and returns rename proposals without modifying any files.
        /// Only items whose current name differs from the Plex-compliant target are included.
        /// </summary>
        public async Task<List<RenameProposal>> ScanTvShowsFolderAsync(string tvShowsRoot)
        {
            var proposals = new List<RenameProposal>();
            if (!Directory.Exists(tvShowsRoot))
                return proposals;

            foreach (var file in Directory.GetFiles(tvShowsRoot).Where(FileNameParser.IsVideoTvShowEpisode))
                await ScanEpisodeFileAsync(tvShowsRoot, file, proposals).ConfigureAwait(false);

            foreach (var dir in Directory.GetDirectories(tvShowsRoot))
            {
                await ScanSubfolderAsync(tvShowsRoot, dir, proposals).ConfigureAwait(false);
            }

            return proposals;
        }

        /// <summary>Processes all TV episodes in a folder, applying Plex naming conventions and renaming or moving files. Skips uncertain TMDB matches — use <see cref="ScanTvShowsFolderAsync"/> to preview and review those first.</summary>
        public async Task ProcessTvShowsFolderAsync(string tvShowsRoot)
        {
            if (!Directory.Exists(tvShowsRoot))
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}TV folder not found: {tvShowsRoot}", LogLevel.Error);
                return;
            }

            LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Scanning TV folder: {tvShowsRoot}", LogLevel.Info);

            int skippedFiles = 0;
            foreach (var file in Directory.GetFiles(tvShowsRoot).Where(FileNameParser.IsVideoTvShowEpisode))
            {
                if (!_createFolders && FileNameParser.IsPlexFormatted(Path.GetFileName(file))) { skippedFiles++; continue; }
                await ProcessEpisodeFileAsync(tvShowsRoot, file).ConfigureAwait(false);
            }
            if (skippedFiles > 0)
                LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}Skipped {skippedFiles} already Plex-formatted episode(s)");

            foreach (var dir in Directory.GetDirectories(tvShowsRoot))
            {
                await ProcessSubfolderAsync(tvShowsRoot, dir).ConfigureAwait(false);
            }
        }

        private async Task ScanSubfolderAsync(string tvShowsRoot, string dirPath, List<RenameProposal> proposals, int depth = 0)
        {
            if (depth > MaxSubfolderDepth)
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipping '{dirPath}' - exceeded max folder depth ({MaxSubfolderDepth})", LogLevel.Warn);
                return;
            }

            foreach (var file in Directory.GetFiles(dirPath).Where(FileNameParser.IsVideoTvShowEpisode))
                await ScanEpisodeFileAsync(tvShowsRoot, file, proposals).ConfigureAwait(false);

            foreach (var subDir in Directory.GetDirectories(dirPath))
                await ScanSubfolderAsync(tvShowsRoot, subDir, proposals, depth + 1).ConfigureAwait(false);
        }

        private async Task ScanEpisodeFileAsync(string tvShowsRoot, string filePath, List<RenameProposal> proposals)
        {
            var fileName = Path.GetFileName(filePath);
            if (!_createFolders && FileNameParser.IsPlexFormatted(fileName)) return;

            var episodeInfo = FileNameParser.ParseTvShowEpisode(fileName);
            if (episodeInfo == null) return;

            var (showInfo, isConfident) = await GetOrLookupShowAsync(episodeInfo.ShowName, episodeInfo.Year).ConfigureAwait(false);
            if (showInfo == null)
            {
                proposals.Add(new RenameProposal(MediaTypeTvShow, filePath, string.Empty, IsConfident: false, IsMatched: false));
                return;
            }

            var ext             = Path.GetExtension(filePath);
            var showFolderName  = FileNameParser.FormatPlexName(showInfo.Title, showInfo.Year);
            var episodeFileName = $"{showFolderName} - S{episodeInfo.Season:D2}E{episodeInfo.Episode:D2}{ext}";

            string proposedPath = _createFolders
                ? Path.Combine(tvShowsRoot, showFolderName, $"Season {episodeInfo.Season:D2}", episodeFileName)
                : Path.Combine(tvShowsRoot, episodeFileName);

            if (!string.Equals(filePath, proposedPath, StringComparison.OrdinalIgnoreCase))
                proposals.Add(new RenameProposal(MediaTypeTvShow, filePath, proposedPath, isConfident));
        }

        private async Task ProcessSubfolderAsync(string tvShowsRoot, string dirPath, int depth = 0)
        {
            if (depth > MaxSubfolderDepth)
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipping '{dirPath}' - exceeded max folder depth ({MaxSubfolderDepth})", LogLevel.Warn);
                return;
            }

            foreach (var file in Directory.GetFiles(dirPath).Where(FileNameParser.IsVideoTvShowEpisode))
                await ProcessEpisodeFileAsync(tvShowsRoot, file).ConfigureAwait(false);

            foreach (var subDir in Directory.GetDirectories(dirPath))
                await ProcessSubfolderAsync(tvShowsRoot, subDir, depth + 1).ConfigureAwait(false);
        }

        private async Task ProcessEpisodeFileAsync(string tvShowsRoot, string filePath)
        {
            var fileName = Path.GetFileName(filePath);

            if (!_createFolders && FileNameParser.IsPlexFormatted(fileName)) return;

            var episodeInfo = FileNameParser.ParseTvShowEpisode(fileName);

            if (episodeInfo == null)
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipped '{fileName}' - not a recognised episode", LogLevel.Info);
                return;
            }

            LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Processing '{fileName}'", LogLevel.Info);
            LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}TvShowRenamer.ProcessEpisodeFile: Parsed show='{episodeInfo.ShowName}' S{episodeInfo.Season:D2}E{episodeInfo.Episode:D2}");

            var (showInfo, isConfident) = await GetOrLookupShowAsync(episodeInfo.ShowName, episodeInfo.Year).ConfigureAwait(false);

            if (showInfo == null) return;
            if (!isConfident)
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipping '{fileName}' - uncertain TMDB match, review in Media Manager", LogLevel.Warn);
                return;
            }

            var ext             = Path.GetExtension(filePath);
            var showFolderName  = FileNameParser.FormatPlexName(showInfo.Title, showInfo.Year);
            var episodeFileName = $"{showFolderName} - S{episodeInfo.Season:D2}E{episodeInfo.Episode:D2}{ext}";

            MoveEpisodeFile(tvShowsRoot, filePath, episodeInfo, showFolderName, episodeFileName);
        }

        // Moves or renames the episode file to its Plex-compliant target path, creating season folders if needed.
        private void MoveEpisodeFile(string tvShowsRoot, string filePath, TvShowEpisodeInfo episodeInfo, string showFolderName, string episodeFileName)
        {
            string targetPath = _createFolders
                ? Path.Combine(tvShowsRoot, showFolderName, $"Season {episodeInfo.Season:D2}", episodeFileName)
                : Path.Combine(tvShowsRoot, episodeFileName);

            if (string.Equals(filePath, targetPath, StringComparison.OrdinalIgnoreCase)) return;

            LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Renaming to {Path.GetRelativePath(tvShowsRoot, targetPath)}", LogLevel.Info);

            if (!_dryRun)
                MediaManagerService.MoveFile(filePath, targetPath);
        }

        // Returns a cached show lookup or performs a new TMDB search and caches the result
        private async Task<(TvShowInfo? Info, bool IsConfident)> GetOrLookupShowAsync(string showName, int? year)
        {
            if (_showCache.TryGetValue(showName, out var cached))
                return cached;

            var result = await LookupTvShowAsync(showName, year).ConfigureAwait(false);
            if (result.Info != null)
                _showCache[showName] = (result.Info, result.IsConfident);

            return result;
        }

        private async Task<(TvShowInfo? Info, bool IsConfident)> LookupTvShowAsync(string name, int? year)
        {
            try
            {
                bool isConfident = true;

                var info = await _tmdb.SearchTvShowAsync(name, year).ConfigureAwait(false);

                // Retry without year: parsed year may be the season year rather than TMDB's first-air year
                if (info == null && year.HasValue)
                {
                    info = await _tmdb.SearchTvShowAsync(name).ConfigureAwait(false);
                    if (info != null) isConfident = false;
                }

                if (info == null)
                {
                    LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}No TMDB match found for show '{name}'", LogLevel.Warn);
                    return (null, false);
                }

                LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}TvShowRenamer.LookupTvShow: Matched '{info.Title}' ({info.Year}) [tmdb-{info.TmdbId}]");
                return (info, isConfident);
            }
            catch (HttpRequestException ex)
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}TMDB TV show lookup failed: {ex.Message}", LogLevel.Error);
                return (null, false);
            }
        }
    }
}
