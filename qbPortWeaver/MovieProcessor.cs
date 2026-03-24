namespace qbPortWeaver
{
    /// <summary>
    /// Processes movie files and folders, applying Plex naming conventions and importing them into the library:
    /// Library/Title (Year)/Title (Year).ext  - with folder creation
    /// Library/Title (Year).ext               - without folder creation
    /// Files are transferred via hardlink, copy, or move depending on the configured import mode.
    /// </summary>
    public sealed class MovieProcessor
    {
        private const string MediaTypeMovie = "Movie";

        private readonly TmdbClient _tmdb;
        private readonly bool _dryRun;
        private readonly bool _createFolders;
        private readonly string _libraryPath;
        private readonly ImportMode _importMode;

        // Caches movie lookups (including confidence) to avoid redundant TMDB API calls across scan cycles.
        // Key includes year to distinguish same-titled movies (e.g. "The Thing|1982" vs "The Thing|2011").
        // ConcurrentDictionary: sync cycle and UI scan can overlap.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (MovieInfo? Info, bool IsConfident)> _movieCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Creates a movie processor that imports files into the specified library folder.</summary>
        /// <param name="tmdb">TMDB client for movie metadata lookups.</param>
        /// <param name="dryRun">When true, logs what would happen without importing any files.</param>
        /// <param name="createFolders">When true, imports files into Plex-recommended subfolders.</param>
        /// <param name="libraryPath">Target library folder for imported movies.</param>
        /// <param name="importMode">Determines how files are transferred: hardlink, copy, or move.</param>
        public MovieProcessor(TmdbClient tmdb, bool dryRun, bool createFolders, string libraryPath, ImportMode importMode = ImportMode.Hardlink)
        {
            _tmdb          = tmdb;
            _dryRun        = dryRun;
            _createFolders = createFolders;
            _libraryPath   = libraryPath;
            _importMode    = importMode;
        }

        /// <summary>
        /// Scans pre-classified movie files and directories and returns import proposals without modifying any files.
        /// Only items not yet present in the library are included.
        /// </summary>
        public async Task<List<MediaProposal>> ScanMoviesAsync(string[] movieFiles, string[] movieDirs)
        {
            var proposals = new List<MediaProposal>();

            foreach (var file in movieFiles)
                await ScanStandaloneFileAsync(file, proposals).ConfigureAwait(false);

            foreach (var dir in movieDirs)
            {
                try
                {
                    await ScanMovieFolderAsync(dir, proposals).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped movie folder '{Path.GetFileName(dir)}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                }
            }

            return proposals;
        }

        /// <summary>Processes pre-classified movie files and directories, importing them into the library with Plex naming conventions. Skips uncertain TMDB matches - use <see cref="ScanMoviesAsync"/> to preview and review those first.</summary>
        public async Task ProcessMoviesAsync(string sourceFolder, string[] movieFiles, string[] movieDirs)
        {
            foreach (var file in movieFiles)
                await ProcessStandaloneFileAsync(sourceFolder, file).ConfigureAwait(false);

            foreach (var dir in movieDirs)
            {
                try
                {
                    await ProcessMovieFolderAsync(sourceFolder, dir).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogManager.Instance.LogMessage($"Skipped movie folder '{Path.GetFileName(dir)}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                }
            }
        }

        private async Task ScanStandaloneFileAsync(string filePath, List<MediaProposal> proposals)
        {
            if (FileImporter.IsAlreadyInLibrary(filePath)) return;

            var fileName = Path.GetFileName(filePath);

            var (title, year) = FileNameParser.ParseMovie(fileName);
            if (string.IsNullOrWhiteSpace(title)) return;

            var (info, isConfident) = await GetOrLookupMovieAsync(title, year).ConfigureAwait(false);
            if (info == null)
            {
                proposals.Add(new MediaProposal(MediaTypeMovie, filePath, string.Empty, IsConfident: false, IsMatched: false));
                return;
            }

            var proposedPath = BuildStandaloneMoviePath(filePath, info);

            if (FileImporter.IsDuplicateFile(filePath, proposedPath)) return;

            if (!string.Equals(filePath, proposedPath, StringComparison.OrdinalIgnoreCase))
                proposals.Add(new MediaProposal(MediaTypeMovie, filePath, proposedPath, isConfident));
        }

        private async Task ScanMovieFolderAsync(string dirPath, List<MediaProposal> proposals)
        {
            var dirName    = Path.GetFileName(dirPath);

            var videoFiles = GetMovieVideoFiles(dirPath);
            if (videoFiles.Count == 0) return;

            // Skip folder if all video files are already in the library
            if (videoFiles.All(FileImporter.IsAlreadyInLibrary)) return;

            var (title, year) = FileNameParser.ParseMovie(dirName);
            if (string.IsNullOrWhiteSpace(title))
                (title, year) = FileNameParser.ParseMovie(Path.GetFileName(videoFiles[0]));
            if (string.IsNullOrWhiteSpace(title)) return;

            var (info, isConfident) = await GetOrLookupMovieAsync(title, year).ConfigureAwait(false);
            if (info == null)
            {
                foreach (var file in videoFiles)
                    proposals.Add(new MediaProposal(MediaTypeMovie, file, string.Empty, IsConfident: false, IsMatched: false));
                return;
            }

            foreach (var file in videoFiles)
            {
                var proposedPath = BuildFolderMoviePath(file, info);

                if (FileImporter.IsDuplicateFile(file, proposedPath)) continue;

                if (!string.Equals(file, proposedPath, StringComparison.OrdinalIgnoreCase))
                    proposals.Add(new MediaProposal(MediaTypeMovie, file, proposedPath, isConfident));
            }
        }

        private async Task ProcessStandaloneFileAsync(string sourceFolder, string filePath)
        {
            if (FileImporter.IsAlreadyInLibrary(filePath)) return;

            var fileName = Path.GetFileName(filePath);

            var (title, year) = FileNameParser.ParseMovie(fileName);

            if (string.IsNullOrWhiteSpace(title))
            {
                LogManager.Instance.LogDebug($"MovieProcessor.ProcessStandaloneFileAsync: Skipped '{fileName}' - could not parse title", Subsystem.MediaManager);
                return;
            }

            var (info, isConfident) = await GetOrLookupMovieAsync(title, year).ConfigureAwait(false);
            if (info == null) return;
            if (!isConfident)
            {
                LogManager.Instance.LogMessage($"Skipped '{fileName}' - uncertain TMDB match, review in Media Manager", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            var targetPath = BuildStandaloneMoviePath(filePath, info);
            MediaManagerService.ImportFileWithLog(filePath, targetPath, sourceFolder, _dryRun, _importMode);

            ImportCompanionFiles(sourceFolder, filePath, targetPath);
        }

        private async Task ProcessMovieFolderAsync(string sourceFolder, string dirPath)
        {
            var dirName    = Path.GetFileName(dirPath);

            var videoFiles = GetMovieVideoFiles(dirPath);

            if (videoFiles.Count == 0)
                return;

            // Skip folder if all video files are already in the library
            if (videoFiles.All(FileImporter.IsAlreadyInLibrary)) return;

            var (title, year) = FileNameParser.ParseMovie(dirName);

            // Fall back to first video filename if folder name yields nothing
            if (string.IsNullOrWhiteSpace(title))
                (title, year) = FileNameParser.ParseMovie(Path.GetFileName(videoFiles[0]));

            if (string.IsNullOrWhiteSpace(title))
            {
                LogManager.Instance.LogDebug($"MovieProcessor.ProcessMovieFolderAsync: Skipped folder '{dirName}' - could not parse title", Subsystem.MediaManager);
                return;
            }

            var (info, isConfident) = await GetOrLookupMovieAsync(title, year).ConfigureAwait(false);
            if (info == null) return;
            if (!isConfident)
            {
                LogManager.Instance.LogMessage($"Skipped folder '{dirName}' - uncertain TMDB match, review in Media Manager", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            // Import each video file individually into the new Plex-named folder
            foreach (var file in videoFiles)
                MediaManagerService.ImportFileWithLog(file, BuildFolderMoviePath(file, info), sourceFolder, _dryRun, _importMode);

            // Import companion files (subtitles etc.) alongside the video files
            var plexFolderName = FileNameParser.FormatPlexName(info.Title, info.Year);
            ImportFolderCompanionFiles(sourceFolder, dirPath, Path.GetFileNameWithoutExtension(videoFiles[0]), plexFolderName);
        }

        // Builds the library target path for a standalone movie file
        private string BuildStandaloneMoviePath(string filePath, MovieInfo info)
        {
            var ext      = Path.GetExtension(filePath);
            var plexName = FileNameParser.FormatPlexName(info.Title, info.Year);
            return _createFolders
                ? Path.Combine(_libraryPath, plexName, $"{plexName}{ext}")
                : Path.Combine(_libraryPath, $"{plexName}{ext}");
        }

        // Builds the library target path for a movie file inside a folder (supports multi-part suffixes)
        private string BuildFolderMoviePath(string filePath, MovieInfo info)
        {
            var ext            = Path.GetExtension(filePath);
            var plexFolderName = FileNameParser.FormatPlexName(info.Title, info.Year);
            var partSuffix     = ExtractPartSuffix(Path.GetFileName(filePath));
            var newFileName    = partSuffix != null
                ? $"{plexFolderName} - {partSuffix}{ext}"
                : $"{plexFolderName}{ext}";
            return Path.Combine(_libraryPath, plexFolderName, newFileName);
        }

        private void ImportCompanionFiles(string sourceFolder, string videoPath, string targetVideoPath) =>
            MediaManagerService.ImportCompanionFiles(sourceFolder, videoPath, targetVideoPath, _dryRun, _importMode);

        // Imports subtitle files from a movie folder, renaming them to match the Plex folder name
        private void ImportFolderCompanionFiles(string sourceFolder, string sourceDir, string firstVideoBase, string plexFolderName)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(sourceDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogManager.Instance.LogMessage($"Skipped companion files in '{Path.GetFileName(sourceDir)}': {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                return;
            }

            foreach (var file in files.Where(FileNameParser.IsSubtitleFile))
            {
                var fileName = Path.GetFileName(file);
                if (!fileName.StartsWith(firstVideoBase, StringComparison.OrdinalIgnoreCase)) continue;

                var suffix     = fileName[firstVideoBase.Length..];
                var targetPath = Path.Combine(_libraryPath, plexFolderName, plexFolderName + suffix);
                MediaManagerService.ImportFileWithLog(file, targetPath, sourceFolder, _dryRun, _importMode);
            }
        }

        // Returns the video files in a folder that are not TV shows
        private static List<string> GetMovieVideoFiles(string dirPath) =>
            Directory.GetFiles(dirPath).Where(f => FileNameParser.IsVideoFile(f) && !FileNameParser.IsTvShow(Path.GetFileName(f))).ToList();

        // Returns a cached movie lookup or performs a new TMDB search and caches the result
        private async Task<(MovieInfo? Info, bool IsConfident)> GetOrLookupMovieAsync(string title, int? year)
        {
            var cacheKey = $"{title}|{year}";
            if (_movieCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var result = await LookupMovieAsync(title, year).ConfigureAwait(false);
            _movieCache.TryAdd(cacheKey, result);
            return result;
        }

        private async Task<(MovieInfo? Info, bool IsConfident)> LookupMovieAsync(string title, int? year)
        {
            try
            {
                bool isConfident = true;

                var info = await _tmdb.SearchMovieAsync(title, year).ConfigureAwait(false);

                // Retry without year: parsed year may not match TMDB's release year
                if (info == null && year.HasValue)
                {
                    info = await _tmdb.SearchMovieAsync(title).ConfigureAwait(false);
                    if (info != null) isConfident = false;
                }

                (info, isConfident) = await TryFallbackLookupsAsync(title, year, info, isConfident).ConfigureAwait(false);

                if (info == null)
                {
                    LogManager.Instance.LogMessage($"No TMDB match found for movie '{title}'", LogLevel.Warn, Subsystem.MediaManager);
                    return (null, false);
                }

                LogManager.Instance.LogDebug($"MovieProcessor.LookupMovieAsync: Matched '{info.Title}' ({info.Year}) [tmdb-{info.TmdbId}]", Subsystem.MediaManager);
                return (info, isConfident);
            }
            catch (HttpRequestException ex)
            {
                LogManager.Instance.LogMessage($"Failed to look up TMDB movie: {ex.Message}", LogLevel.Error, Subsystem.MediaManager);
                return (null, false);
            }
        }

        // Applies two fallback TMDB lookup strategies to reduce unmatched results.
        // After-dash strategy: only runs when info is null (no match from initial lookups).
        // Trailing-number strategy: runs when info is null OR info.Year is null.
        private async Task<(MovieInfo? Info, bool IsConfident)> TryFallbackLookupsAsync(
            string title, int? year, MovieInfo? info, bool isConfident)
        {
            // Try the part after " - " (e.g. "Harry Potter 1 - The Sorcerer's Stone")
            if (info == null && title.Contains(" - "))
            {
                var afterDash = title[(title.IndexOf(" - ", StringComparison.Ordinal) + 3)..].Trim();
                LogManager.Instance.LogDebug($"MovieProcessor.TryFallbackLookupsAsync: Retrying with '{afterDash}'", Subsystem.MediaManager);
                info = await _tmdb.SearchMovieAsync(afterDash, year).ConfigureAwait(false);
                if (info != null) isConfident = false;
            }

            // Try without trailing number (e.g. "Shrek 1" -> "Shrek")
            if (info == null || (info.Year == null && title.Length > 2))
            {
                var trimmed = title.TrimEnd();
                if (trimmed.Length > 2 && char.IsDigit(trimmed[^1]) && trimmed[^2] == ' ')
                {
                    var withoutNum = trimmed[..^2].Trim();
                    LogManager.Instance.LogDebug($"MovieProcessor.TryFallbackLookupsAsync: Retrying without trailing number '{withoutNum}'", Subsystem.MediaManager);
                    var altInfo = await _tmdb.SearchMovieAsync(withoutNum, year).ConfigureAwait(false);
                    if (altInfo?.Year != null)
                    {
                        info        = altInfo;
                        isConfident = false;
                    }
                }
            }

            return (info, isConfident);
        }

        // Detects multi-part suffixes such as "cd1", "pt2", "disc3" and returns the normalised token.
        // Requires a word boundary before the pattern to avoid matching mid-word (e.g. "Arcade2").
        private static string? ExtractPartSuffix(string fileName)
        {
            var name     = Path.GetFileNameWithoutExtension(fileName);
            string[] patterns = ["cd", "disc", "disk", "dvd", "part", "pt"];

            foreach (var pattern in patterns)
            {
                var idx = name.LastIndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;

                // Ensure the match is at a word boundary, not embedded in a longer word
                if (idx > 0 && char.IsLetter(name[idx - 1])) continue;

                var after = name[(idx + pattern.Length)..].Trim();
                if (after.Length > 0 && char.IsDigit(after[0]))
                    return $"{pattern}{after[0]}";
            }

            return null;
        }
    }
}
