namespace qbPortWeaver;

/// <summary>
/// Processes TV episode files, applying Plex naming conventions and importing them into the library:
/// Library/Show Name (Year)/Season XX/Show Name (Year) - SXXEXX.ext  - with folder creation
/// Library/Show Name (Year) - SXXEXX.ext                              - without folder creation
/// Files are transferred via hardlink, copy, or move depending on the configured import mode.
/// </summary>
/// <param name="tmdb">TMDB client for TV show metadata lookups.</param>
/// <param name="dryRun">When true, logs what would happen without importing any files.</param>
/// <param name="createFolders">When true, imports files into Plex-recommended season subfolders.</param>
/// <param name="libraryPath">Target library folder for imported TV shows.</param>
/// <param name="importMode">Determines how files are transferred: hardlink, copy, or move.</param>
public sealed class TvShowProcessor(TmdbClient tmdb, bool dryRun, bool createFolders, string libraryPath, ImportMode importMode = ImportMode.Hardlink)
{
    /// <summary>
    /// Scans pre-classified TV episode files and returns import proposals without modifying any files.
    /// Only items not yet present in the library are included.
    /// </summary>
    public async Task<List<MediaProposal>> ScanTvShowsAsync(string[] tvShowFiles, Action? onItemProcessed = null, CancellationToken cancellationToken = default)
    {
        var proposals = new List<MediaProposal>();

        foreach (var file in tvShowFiles)
        {
            await ScanEpisodeFileAsync(file, proposals, cancellationToken).ConfigureAwait(false);
            onItemProcessed?.Invoke();
        }

        return proposals;
    }

    /// <summary>Processes pre-classified TV episode files, importing them into the library with Plex naming conventions. Skips uncertain TMDB matches - use <see cref="ScanTvShowsAsync"/> to preview and review those first.</summary>
    public async Task ProcessTvShowsAsync(string[] tvShowFiles, CancellationToken cancellationToken = default)
    {
        foreach (var file in tvShowFiles)
            await ProcessEpisodeFileAsync(file, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Scans folder-classified episodes (show name from grandparent folder, season from parent folder, episode from filename prefix) and returns import proposals.</summary>
    public async Task<List<MediaProposal>> ScanFolderClassifiedAsync(FolderClassifiedEpisode[] episodes, Action? onItemProcessed = null, CancellationToken cancellationToken = default)
    {
        var proposals = new List<MediaProposal>();
        foreach (var ep in episodes)
        {
            await ScanFolderClassifiedEpisodeAsync(ep, proposals, cancellationToken).ConfigureAwait(false);
            onItemProcessed?.Invoke();
        }
        return proposals;
    }

    /// <summary>Processes folder-classified episodes, importing them into the library with Plex naming conventions. Skips uncertain TMDB matches - use <see cref="ScanFolderClassifiedAsync"/> to preview those first.</summary>
    public async Task ProcessFolderClassifiedAsync(FolderClassifiedEpisode[] episodes, CancellationToken cancellationToken = default)
    {
        foreach (var ep in episodes)
            await ProcessFolderClassifiedEpisodeAsync(ep, cancellationToken).ConfigureAwait(false);
    }

    // Scans one folder-classified episode. Show name comes from the grandparent folder
    // (already resolved during classification), so no filename parse is needed - the
    // resolved TvShowEpisodeInfo goes straight to the shared post-resolution flow.
    private Task ScanFolderClassifiedEpisodeAsync(FolderClassifiedEpisode ep, List<MediaProposal> proposals, CancellationToken cancellationToken)
    {
        var (showName, year) = ResolveShowAndYear(ep.ShowName);
        var episodeInfo = new TvShowEpisodeInfo(showName, year, ep.Season, ep.Episode);
        return ScanResolvedEpisodeAsync(ep.FilePath, episodeInfo, proposals, cancellationToken);
    }

    private Task ProcessFolderClassifiedEpisodeAsync(FolderClassifiedEpisode ep, CancellationToken cancellationToken)
    {
        var (showName, year) = ResolveShowAndYear(ep.ShowName);
        var episodeInfo = new TvShowEpisodeInfo(showName, year, ep.Season, ep.Episode);
        return ProcessResolvedEpisodeAsync(ep.FilePath, episodeInfo, cancellationToken);
    }

    // Parses the show folder name as a "Title (Year)" pair. Falls back to the raw folder name
    // if no title can be parsed (e.g. a single-word title that ParseTitleYear strips to empty).
    private static (string ShowName, int? Year) ResolveShowAndYear(string rawShowFolder)
    {
        var (title, year) = FileNameParser.ParseTitleYear(rawShowFolder);
        return string.IsNullOrWhiteSpace(title) ? (rawShowFolder, year) : (title, year);
    }

    // Scans a single TV episode file and adds proposals for unmatched or new items
    private Task ScanEpisodeFileAsync(string filePath, List<MediaProposal> proposals, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(filePath);

        var episodeInfo = FileNameParser.ParseTvShowEpisode(fileName);
        if (episodeInfo is null)
        {
            LogManager.Instance.LogDebug($"TvShowProcessor.ScanEpisodeFileAsync: Skipped '{fileName}' - not a recognized episode", Subsystem.MediaManager);
            return Task.CompletedTask;
        }

        return ScanResolvedEpisodeAsync(filePath, episodeInfo, proposals, cancellationToken);
    }

    // Imports a single TV episode file into the library
    private Task ProcessEpisodeFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(filePath);

        var episodeInfo = FileNameParser.ParseTvShowEpisode(fileName);
        if (episodeInfo is null)
        {
            LogManager.Instance.LogDebug($"TvShowProcessor.ProcessEpisodeFileAsync: Skipped '{fileName}' - not a recognized episode", Subsystem.MediaManager);
            return Task.CompletedTask;
        }

        return ProcessResolvedEpisodeAsync(filePath, episodeInfo, cancellationToken);
    }

    // Shared post-resolution scan: takes an already-resolved TvShowEpisodeInfo (from filename
    // parsing OR folder classification) and builds the proposal. Centralises the TMDB lookup,
    // destination-matches check, and proposal construction so both source paths stay in sync.
    private async Task ScanResolvedEpisodeAsync(string filePath, TvShowEpisodeInfo episodeInfo, List<MediaProposal> proposals, CancellationToken cancellationToken)
    {
        var (info, isConfident) = await GetOrLookupTvShowAsync(episodeInfo.ShowName, episodeInfo.Year, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            proposals.Add(new MediaProposal(MediaProposal.TypeTvShow, filePath, string.Empty, IsConfident: false, IsMatched: false));
            return;
        }

        var proposedPath = BuildEpisodePath(filePath, info, episodeInfo, libraryPath, createFolders);

        if (MediaImporter.DestinationMatchesSource(filePath, proposedPath)) return;

        if (!string.Equals(filePath, proposedPath, StringComparison.OrdinalIgnoreCase))
            proposals.Add(new MediaProposal(MediaProposal.TypeTvShow, filePath, proposedPath, isConfident,
                PosterPath: info.PosterPath, TmdbId: info.TmdbId, VoteCount: info.VoteCount, Overview: info.Overview));
    }

    // Shared post-resolution import: looks up TMDB, skips on uncertain matches, and imports
    // the file plus companion subtitles. Counterpart to ScanResolvedEpisodeAsync.
    private async Task ProcessResolvedEpisodeAsync(string filePath, TvShowEpisodeInfo episodeInfo, CancellationToken cancellationToken)
    {
        var (info, isConfident) = await GetOrLookupTvShowAsync(episodeInfo.ShowName, episodeInfo.Year, cancellationToken).ConfigureAwait(false);
        if (info is null) return;
        if (!isConfident)
        {
            // Transition-only, keyed on the full path - see MovieProcessor.ShouldImportMatch, which
            // emits this same sentence, and MediaManagerService.UncertainMatchStateKeyPrefix.
            LogManager.Instance.LogStateChange(
                MediaManagerService.UncertainMatchStateKeyPrefix + filePath,
                $"Skipped '{Path.GetFileName(filePath)}' - uncertain TMDB match, review in Media Manager",
                LogLevel.Warn, Subsystem.MediaManager);
            return;
        }

        var targetPath = BuildEpisodePath(filePath, info, episodeInfo, libraryPath, createFolders);
        MediaManagerService.ImportFile(filePath, targetPath, dryRun, importMode);
        MediaManagerService.ImportCompanionFiles(filePath, targetPath, dryRun, importMode);
    }

    // Builds the library target path for an episode file.
    // Multi-episode files (e.g. S01E01E02) are named with both episode numbers: "Show (Year) - S01E01E02.ext"
    // Exposed for MediaManagerForm rematch logic to share the same naming convention.
    internal static string BuildEpisodePath(string filePath, TvShowInfo info, TvShowEpisodeInfo episodeInfo, string libraryPath, bool createFolders)
    {
        var ext = Path.GetExtension(filePath);
        var plexName = FileNameParser.FormatPlexName(info.Title, info.Year);
        var episodeCode = episodeInfo.EndEpisode.HasValue
            ? $"S{episodeInfo.Season:D2}E{episodeInfo.Episode:D2}E{episodeInfo.EndEpisode.Value:D2}"
            : $"S{episodeInfo.Season:D2}E{episodeInfo.Episode:D2}";
        var episodeFileName = $"{plexName} - {episodeCode}{ext}";

        return createFolders
            ? Path.Combine(libraryPath, plexName, $"Season {episodeInfo.Season:D2}", episodeFileName)
            : Path.Combine(libraryPath, episodeFileName);
    }

    // Returns a cached TV show lookup or performs a new TMDB search, deduplicating concurrent lookups
    // for the same show so parallel source-folder scans share one TMDB API call.
    private Task<(TvShowInfo? Info, bool IsConfident)> GetOrLookupTvShowAsync(string title, int? year, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{title}|{year}";
        return TmdbCacheManager.GetOrComputeTvShowAsync(cacheKey, async () =>
        {
            var result = await TmdbClient.LookupAsync((title, year),
                tmdb.SearchTvShowCandidatesAsync,
                i => i.Year is not null, i => i.Title, i => i.VoteCount, "TV show", cancellationToken).ConfigureAwait(false);
            if (result.Info is not null)
                LogManager.Instance.LogDebug($"TvShowProcessor.GetOrLookupTvShowAsync: Matched '{result.Info.Title}' ({result.Info.Year}) [tmdb-{result.Info.TmdbId}]", Subsystem.MediaManager);
            return result;
        });
    }
}
