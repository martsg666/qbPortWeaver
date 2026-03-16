namespace qbPortWeaver
{
    /// <summary>Orchestrates media file renaming on each sync cycle when the Media Manager feature is enabled.</summary>
    public static class MediaManagerService
    {
        /// <summary>
        /// Runs one media scan cycle. Returns immediately if the feature is disabled or the TMDB API key is not configured.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        public static async Task RunAsync(CancellationToken cancellationToken)
        {
            if (!RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaEnabled))
                return;

            var apiKey = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyTmdbApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}TMDB API key not configured - skipping scan", LogLevel.Warn);
                return;
            }

            bool dryRun        = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaDryRun);
            bool createFolders = RegistrySettingsManager.GetBool(RegistrySettingsManager.SectionMedia, RegistrySettingsManager.KeyMediaCreateFolders);

            LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Scan started (dryRun={dryRun}, createFolders={createFolders})", LogLevel.Info);

            var tmdb         = new TmdbClient(apiKey);
            var movieRenamer = new MovieRenamer(tmdb, dryRun, createFolders);
            var tvShowRenamer = new TvShowRenamer(tmdb, dryRun, createFolders);

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

            LogManager.Instance.LogMessage($"{AppConstants.MediaManagerLogPrefix}Scan complete", LogLevel.Info);
        }

        /// <summary>
        /// Returns rename proposals for all configured folders without modifying any files.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="apiKey">TMDB API key used to look up movie and TV show metadata.</param>
        /// <param name="createFolders">When true, proposals include moving files into Plex-recommended subfolders.</param>
        /// <param name="movieFolders">Root folders to scan for movie files.</param>
        /// <param name="tvShowFolders">Root folders to scan for TV episode files.</param>
        /// <param name="cancellationToken">Token to cancel the scan between folders.</param>
        public static async Task<List<RenameProposal>> ScanAsync(string apiKey, bool createFolders, string[] movieFolders, string[] tvShowFolders, CancellationToken cancellationToken)
        {
            var proposals = new List<RenameProposal>();

            var tmdb = new TmdbClient(apiKey);
            // dryRun is irrelevant for scan - scan methods never touch files
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
        /// Applies a set of rename proposals, respecting any user edits made to the proposed paths in the UI grid.
        /// Proposals are typically produced by <see cref="ScanAsync"/> but may have been modified by the user before calling this method.
        /// Throws <see cref="OperationCanceledException"/> if <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="proposals">The rename proposals to apply. Each proposal's <see cref="RenameProposal.ProposedPath"/> is used as the rename target.</param>
        /// <param name="cancellationToken">Token to cancel the operation between renames.</param>
        public static Task ApplyProposalsAsync(IEnumerable<RenameProposal> proposals, CancellationToken cancellationToken)
            => Task.Run(() =>
            {
                foreach (var proposal in proposals)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LogManager.Instance.LogMessage(
                        $"{AppConstants.MediaManagerLogPrefix}Renaming '{proposal.OriginalPath}' -> '{proposal.ProposedPath}'",
                        LogLevel.Info);
                    try
                    {
                        if (File.Exists(proposal.ProposedPath))
                        {
                            LogManager.Instance.LogMessage(
                                $"{AppConstants.MediaManagerLogPrefix}Skipped rename - target already exists: '{Path.GetFileName(proposal.ProposedPath)}'",
                                LogLevel.Warn);
                            continue;
                        }
                        var targetDir = Path.GetDirectoryName(proposal.ProposedPath);
                        if (!string.IsNullOrEmpty(targetDir))
                            Directory.CreateDirectory(targetDir);
                        File.Move(proposal.OriginalPath, proposal.ProposedPath);
                        LogManager.Instance.LogDebug($"{AppConstants.MediaManagerLogPrefix}MediaManagerService.ApplyProposals: Renamed OK '{Path.GetFileName(proposal.ProposedPath)}'");
                    }
                    catch (Exception ex)
                    {
                        LogManager.Instance.LogMessage(
                            $"{AppConstants.MediaManagerLogPrefix}Failed to rename '{Path.GetFileName(proposal.OriginalPath)}': {ex.Message}",
                            LogLevel.Error);
                    }
                }
            }, cancellationToken);

        private static string[] GetFolders(string key)
        {
            var value = RegistrySettingsManager.GetValue(RegistrySettingsManager.SectionMedia, key);
            if (string.IsNullOrWhiteSpace(value))
                return [];
            return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }
}
