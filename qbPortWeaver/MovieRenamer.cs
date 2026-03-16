namespace qbPortWeaver
{
    /// <summary>
    /// Renames movie files and folders to Plex naming convention:
    /// Movies/Title (Year)/Title (Year).ext  - with folder creation
    /// Movies/Title (Year).ext               - without folder creation
    /// </summary>
    public sealed class MovieRenamer
    {
        private const string MediaTypeMovie = "Movie";

        private readonly TmdbClient _tmdb;
        private readonly bool _dryRun;
        private readonly bool _createFolders;

        public MovieRenamer(TmdbClient tmdb, bool dryRun, bool createFolders)
        {
            _tmdb          = tmdb;
            _dryRun        = dryRun;
            _createFolders = createFolders;
        }

        /// <summary>
        /// Scans a movies folder and returns rename proposals without modifying any files.
        /// Only items whose current name differs from the Plex-compliant target are included.
        /// </summary>
        public async Task<List<RenameProposal>> ScanMoviesFolderAsync(string moviesRoot)
        {
            var proposals = new List<RenameProposal>();

            if (!Directory.Exists(moviesRoot))
                return proposals;

            foreach (var file in Directory.GetFiles(moviesRoot).Where(FileNameParser.IsVideoFile))
                await ScanStandaloneFileAsync(moviesRoot, file, proposals).ConfigureAwait(false);

            foreach (var dir in Directory.GetDirectories(moviesRoot))
            {
                await ScanMovieFolderAsync(moviesRoot, dir, proposals).ConfigureAwait(false);
            }

            return proposals;
        }

        /// <summary>Processes all movies in a folder, applying Plex naming conventions and renaming or moving files. Skips uncertain TMDB matches — use <see cref="ScanMoviesFolderAsync"/> to preview and review those first.</summary>
        public async Task ProcessMoviesFolderAsync(string moviesRoot)
        {
            if (!Directory.Exists(moviesRoot))
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Movie folder not found: {moviesRoot}", LogLevel.Error);
                return;
            }

            LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Scanning movie folder: {moviesRoot}", LogLevel.Info);

            foreach (var file in Directory.GetFiles(moviesRoot).Where(FileNameParser.IsVideoFile))
                await ProcessStandaloneFileAsync(moviesRoot, file).ConfigureAwait(false);

            foreach (var dir in Directory.GetDirectories(moviesRoot))
            {
                await ProcessMovieFolderAsync(moviesRoot, dir).ConfigureAwait(false);
            }
        }

        private async Task ScanStandaloneFileAsync(string root, string filePath, List<RenameProposal> proposals)
        {
            var fileName = Path.GetFileName(filePath);
            // In folder mode a flat Plex-named file still needs moving into its subfolder
            if (!_createFolders && FileNameParser.IsPlexFormatted(fileName)) return;

            var (title, year) = FileNameParser.Parse(fileName);
            if (string.IsNullOrWhiteSpace(title)) return;

            var (info, isConfident) = await LookupMovieAsync(title, year).ConfigureAwait(false);
            if (info == null)
            {
                proposals.Add(new RenameProposal(MediaTypeMovie, filePath, string.Empty, IsConfident: false, IsMatched: false));
                return;
            }

            var ext      = Path.GetExtension(filePath);
            var plexName = FileNameParser.SanitizeFileName($"{info.Title} ({info.Year})");

            string proposedPath = _createFolders
                ? Path.Combine(root, plexName, $"{plexName}{ext}")
                : Path.Combine(root, $"{plexName}{ext}");

            if (!string.Equals(filePath, proposedPath, StringComparison.OrdinalIgnoreCase))
                proposals.Add(new RenameProposal(MediaTypeMovie, filePath, proposedPath, isConfident));
        }

        private async Task ScanMovieFolderAsync(string root, string dirPath, List<RenameProposal> proposals)
        {
            var dirName    = Path.GetFileName(dirPath);
            if (FileNameParser.IsPlexFormatted(dirName)) return;

            var videoFiles = Directory.GetFiles(dirPath).Where(FileNameParser.IsVideoFile).ToList();
            if (videoFiles.Count == 0) return;

            var (title, year) = FileNameParser.Parse(dirName);
            if (string.IsNullOrWhiteSpace(title))
                (title, year) = FileNameParser.Parse(Path.GetFileName(videoFiles[0]));
            if (string.IsNullOrWhiteSpace(title)) return;

            var (info, isConfident) = await LookupMovieAsync(title, year).ConfigureAwait(false);
            if (info == null)
            {
                foreach (var file in videoFiles)
                    proposals.Add(new RenameProposal(MediaTypeMovie, file, string.Empty, IsConfident: false, IsMatched: false));
                return;
            }

            var plexFolderName = FileNameParser.SanitizeFileName($"{info.Title} ({info.Year})");
            var newDirPath     = Path.Combine(root, plexFolderName);

            foreach (var file in videoFiles)
            {
                var ext         = Path.GetExtension(file);
                var partSuffix  = ExtractPartSuffix(Path.GetFileName(file));
                var newFileName = partSuffix != null
                    ? $"{plexFolderName} - {partSuffix}{ext}"
                    : $"{plexFolderName}{ext}";
                var proposedPath = Path.Combine(newDirPath, newFileName);

                if (!string.Equals(file, proposedPath, StringComparison.OrdinalIgnoreCase))
                    proposals.Add(new RenameProposal(MediaTypeMovie, file, proposedPath, isConfident));
            }
        }

        private async Task ProcessStandaloneFileAsync(string root, string filePath)
        {
            var fileName = Path.GetFileName(filePath);

            // Skip TMDB lookup entirely if the file is already Plex-formatted (and we're not reorganising into folders)
            if (!_createFolders && FileNameParser.IsPlexFormatted(fileName))
            {
                LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MovieRenamer.ProcessStandaloneFile: already Plex-formatted, skipping '{fileName}'");
                return;
            }

            var (title, year) = FileNameParser.Parse(fileName);

            if (string.IsNullOrWhiteSpace(title))
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipped '{fileName}' - could not parse title", LogLevel.Info);
                return;
            }

            LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Processing '{fileName}'", LogLevel.Info);
            LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MovieRenamer.ProcessStandaloneFile: parsed title='{title}', year={year?.ToString() ?? "unknown"}");

            var (info, isConfident) = await LookupMovieAsync(title, year).ConfigureAwait(false);
            if (info == null) return;
            if (!isConfident)
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipping '{fileName}' - uncertain TMDB match, review in Media Manager", LogLevel.Warn);
                return;
            }

            var ext      = Path.GetExtension(filePath);
            var plexName = FileNameParser.SanitizeFileName($"{info.Title} ({info.Year})");

            if (_createFolders)
            {
                var newFolderPath = Path.Combine(root, plexName);
                var newFilePath   = Path.Combine(newFolderPath, $"{plexName}{ext}");

                if (filePath == newFilePath)
                {
                    LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MovieRenamer.ProcessStandaloneFile: already correctly named");
                    return;
                }

                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Renaming to {plexName}/{plexName}{ext}", LogLevel.Info);
                if (!_dryRun)
                {
                    Directory.CreateDirectory(newFolderPath);
                    MoveFile(filePath, newFilePath, $"{plexName}{ext}");
                    LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MovieRenamer.ProcessStandaloneFile: renamed");
                }
            }
            else
            {
                var newFilePath = Path.Combine(root, $"{plexName}{ext}");

                if (filePath == newFilePath)
                {
                    LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MovieRenamer.ProcessStandaloneFile: already correctly named");
                    return;
                }

                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Renaming to {plexName}{ext}", LogLevel.Info);
                if (!_dryRun)
                {
                    MoveFile(filePath, newFilePath, $"{plexName}{ext}");
                    LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MovieRenamer.ProcessStandaloneFile: renamed");
                }
            }
        }

        private async Task ProcessMovieFolderAsync(string root, string dirPath)
        {
            var dirName    = Path.GetFileName(dirPath);

            // Skip TMDB lookup entirely if the folder is already Plex-formatted
            if (FileNameParser.IsPlexFormatted(dirName))
            {
                LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MovieRenamer.ProcessMovieFolder: already Plex-formatted, skipping folder '{dirName}'");
                return;
            }

            var videoFiles = Directory.GetFiles(dirPath).Where(FileNameParser.IsVideoFile).ToList();

            if (videoFiles.Count == 0)
                return;

            var (title, year) = FileNameParser.Parse(dirName);

            // Fall back to first video filename if folder name yields nothing
            if (string.IsNullOrWhiteSpace(title))
                (title, year) = FileNameParser.Parse(Path.GetFileName(videoFiles[0]));

            if (string.IsNullOrWhiteSpace(title))
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipped folder '{dirName}' - could not parse title", LogLevel.Info);
                return;
            }

            LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Processing folder '{dirName}'", LogLevel.Info);
            LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MovieRenamer.ProcessMovieFolder: parsed title='{title}', year={year?.ToString() ?? "unknown"}");

            var (info, isConfident) = await LookupMovieAsync(title, year).ConfigureAwait(false);
            if (info == null) return;
            if (!isConfident)
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipping folder '{dirName}' - uncertain TMDB match, review in Media Manager", LogLevel.Warn);
                return;
            }

            var plexFolderName = FileNameParser.SanitizeFileName($"{info.Title} ({info.Year})");
            var newDirPath     = Path.Combine(root, plexFolderName);

            LogPlannedRenames(videoFiles, plexFolderName);

            if (string.Equals(dirPath, newDirPath, StringComparison.OrdinalIgnoreCase))
            {
                LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MovieRenamer.ProcessMovieFolder: folder already correctly named");
                return;
            }

            LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Renaming folder '{dirName}' -> '{plexFolderName}'", LogLevel.Info);

            if (_dryRun) return;

            RenameFilesInFolder(dirPath, videoFiles, plexFolderName);

            if (Directory.Exists(newDirPath))
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipped folder rename - target already exists: '{plexFolderName}'", LogLevel.Warn);
            else
            {
                Directory.Move(dirPath, newDirPath);
                LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MovieRenamer.ProcessMovieFolder: renamed");
            }
        }

        // Renames video files to Plex names, then renames companion files via RenameCompanionFilesInFolder
        private static void RenameFilesInFolder(string dirPath, List<string> videoFiles, string plexFolderName)
        {
            foreach (var file in videoFiles)
            {
                var ext         = Path.GetExtension(file);
                var oldFileName = Path.GetFileName(file);
                var partSuffix  = ExtractPartSuffix(oldFileName);
                var newFileName = partSuffix != null
                    ? $"{plexFolderName} - {partSuffix}{ext}"
                    : $"{plexFolderName}{ext}";

                var newFilePath = Path.Combine(dirPath, newFileName);
                if (!string.Equals(file, newFilePath, StringComparison.OrdinalIgnoreCase))
                    MoveFile(file, newFilePath, newFileName);
            }

            RenameCompanionFilesInFolder(dirPath, Path.GetFileNameWithoutExtension(videoFiles[0]), plexFolderName);
        }

        // Renames subtitle and other companion files whose name begins with firstVideoBase to the Plex folder name
        private static void RenameCompanionFilesInFolder(string dirPath, string firstVideoBase, string plexFolderName)
        {
            foreach (var file in Directory.GetFiles(dirPath).Where(f => !FileNameParser.IsVideoFile(f)))
            {
                var fileName = Path.GetFileName(file);
                if (!fileName.StartsWith(firstVideoBase, StringComparison.OrdinalIgnoreCase)) continue;

                var suffix      = fileName[firstVideoBase.Length..];
                var newFilePath = Path.Combine(dirPath, plexFolderName + suffix);
                if (!string.Equals(file, newFilePath, StringComparison.OrdinalIgnoreCase))
                    MoveFile(file, newFilePath, plexFolderName + suffix);
            }
        }

        private static void MoveFile(string sourcePath, string targetPath, string targetName)
        {
            if (File.Exists(targetPath))
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Skipped rename - target already exists: '{targetName}'", LogLevel.Warn);
            else
                File.Move(sourcePath, targetPath);
        }

        // Logs the planned rename for each video file in the folder
        private static void LogPlannedRenames(IEnumerable<string> videoFiles, string plexFolderName)
        {
            foreach (var file in videoFiles)
            {
                var oldFileName = Path.GetFileName(file);
                var partSuffix  = ExtractPartSuffix(oldFileName);
                var newFileName = partSuffix != null
                    ? $"{plexFolderName} - {partSuffix}{Path.GetExtension(file)}"
                    : $"{plexFolderName}{Path.GetExtension(file)}";

                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Renaming '{oldFileName}' -> '{newFileName}'", LogLevel.Info);
            }
        }

        private async Task<(MovieInfo? Info, bool IsConfident)> LookupMovieAsync(string title, int? year)
        {
            try
            {
                bool isConfident = true;

                var info = await _tmdb.SearchMovieAsync(title, year).ConfigureAwait(false);

                if (info == null && year.HasValue)
                {
                    info = await _tmdb.SearchMovieAsync(title).ConfigureAwait(false);
                    if (info != null) isConfident = false;
                }

                (info, isConfident) = await TryFallbackLookupsAsync(title, year, noMatchSoFar: info == null, info, isConfident).ConfigureAwait(false);

                if (info == null)
                {
                    LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}No TMDB match found for '{title}'", LogLevel.Warn);
                    return (null, false);
                }

                LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MovieRenamer.LookupMovie: matched '{info.Title}' ({info.Year}) [tmdb-{info.TmdbId}]");
                return (info, isConfident);
            }
            catch (HttpRequestException ex)
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}TMDB movie lookup failed: {ex.Message}", LogLevel.Error);
                return (null, false);
            }
        }

        // Applies two fallback TMDB lookup strategies to reduce unmatched results.
        // After-dash strategy: only runs when noMatchSoFar=true (info was null after initial lookup).
        // Trailing-number strategy: runs when info==null OR info.Year==null.
        private async Task<(MovieInfo? info, bool isConfident)> TryFallbackLookupsAsync(
            string title, int? year, bool noMatchSoFar, MovieInfo? info, bool isConfident)
        {
            // Try the part after " - " (e.g. "Harry Potter 1 - The Sorcerer's Stone")
            if (noMatchSoFar && title.Contains(" - "))
            {
                var afterDash = title[(title.IndexOf(" - ", StringComparison.Ordinal) + 3)..].Trim();
                LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MovieRenamer.LookupMovie: retrying with '{afterDash}'");
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
                    LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MovieRenamer.LookupMovie: retrying without trailing number '{withoutNum}'");
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
        private static string? ExtractPartSuffix(string fileName)
        {
            var name     = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
            string[] patterns = ["cd", "disc", "disk", "dvd", "part", "pt"];

            foreach (var pattern in patterns)
            {
                var idx = name.LastIndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;

                var after = name[(idx + pattern.Length)..].Trim();
                if (after.Length > 0 && char.IsDigit(after[0]))
                    return $"{pattern}{after[0]}";
            }

            return null;
        }
    }
}
