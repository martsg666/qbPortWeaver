namespace qbPortWeaver
{
    /// <summary>Orchestrates media file renaming on each sync cycle when the Media Manager feature is enabled.</summary>
    public class MediaManagerService
    {
        /// <summary>
        /// Applies a specific set of rename proposals produced by <see cref="ScanAsync"/>, respecting any
        /// user edits made to the proposed paths in the UI grid.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        public Task ApplyProposalsAsync(IEnumerable<RenameProposal> proposals, CancellationToken cancellationToken)
            => Task.Run(() =>
            {
                foreach (var proposal in proposals)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LogManager.Instance.LogMessage(
                        $"[MediaManager] Renaming '{proposal.OriginalPath}' -> '{proposal.ProposedPath}'",
                        LogLevel.Info);
                    try
                    {
                        var targetDir = Path.GetDirectoryName(proposal.ProposedPath);
                        if (!string.IsNullOrEmpty(targetDir))
                            Directory.CreateDirectory(targetDir);
                        File.Move(proposal.OriginalPath, proposal.ProposedPath);
                        LogManager.Instance.LogDebug($"[MediaManager] Renamed OK: '{Path.GetFileName(proposal.ProposedPath)}'");
                    }
                    catch (Exception ex)
                    {
                        LogManager.Instance.LogMessage(
                            $"[MediaManager] Failed to rename '{Path.GetFileName(proposal.OriginalPath)}': {ex.Message}",
                            LogLevel.Error);
                    }
                }
            }, cancellationToken);

        /// <summary>
        /// Returns rename proposals for all configured folders without modifying any files.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        public async Task<List<RenameProposal>> ScanAsync(string apiKey, bool createFolders, string[] movieFolders, string[] tvShowFolders, CancellationToken cancellationToken)
        {
            var proposals = new List<RenameProposal>();

            using var tmdb        = new TmdbClient(apiKey);
            // dryRun flag is irrelevant for scan -scan methods never touch files
            var movieRenamer  = new MovieRenamer(tmdb, dryRun: true, createFolders);
            var tvShowRenamer = new TvShowRenamer(tmdb, dryRun: true, createFolders);

            foreach (var folder in movieFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                proposals.AddRange(await movieRenamer.ScanMoviesFolderAsync(folder).ConfigureAwait(false));
            }

            foreach (var folder in tvShowFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                proposals.AddRange(await tvShowRenamer.ScanTvShowsFolderAsync(folder).ConfigureAwait(false));
            }

            return proposals;
        }

        /// <summary>
        /// Runs one media scan cycle. Returns immediately if the feature is disabled or the TMDB API key is not configured.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            if (!RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaEnabled))
                return;

            var apiKey = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyTmdbApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                LogManager.Instance.LogMessage("[MediaManager] TMDB API key not configured - skipping scan", LogLevel.Warn);
                return;
            }

            bool dryRun        = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDryRun);
            bool createFolders = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaCreateFolders);

            LogManager.Instance.LogMessage($"[MediaManager] Scan started (dryRun={dryRun}, createFolders={createFolders})", LogLevel.Info);

            using var tmdb       = new TmdbClient(apiKey);
            var movieRenamer     = new MovieRenamer(tmdb, dryRun, createFolders);
            var tvShowRenamer    = new TvShowRenamer(tmdb, dryRun, createFolders);

            foreach (var folder in GetFolders(RegistrySettingsManager.KeyMediaMovieFolders))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await movieRenamer.ProcessMoviesFolderAsync(folder).ConfigureAwait(false);
            }

            foreach (var folder in GetFolders(RegistrySettingsManager.KeyMediaTvShowFolders))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await tvShowRenamer.ProcessTvShowsFolderAsync(folder).ConfigureAwait(false);
            }

            LogManager.Instance.LogMessage("[MediaManager] Scan complete", LogLevel.Info);
        }

        private static string[] GetFolders(string key)
        {
            var value = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, key);
            if (string.IsNullOrWhiteSpace(value))
                return [];
            return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }
}
