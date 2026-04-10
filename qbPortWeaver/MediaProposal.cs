namespace qbPortWeaver
{
    /// <summary>Represents a proposed media file import - the original file path and the Plex-compliant target path in the library.</summary>
    /// <param name="MediaType">Short label used in the results grid, e.g. <see cref="TypeMovie"/> or <see cref="TypeTvShow"/>.</param>
    /// <param name="OriginalPath">Absolute path to the existing file in the source folder.</param>
    /// <param name="ProposedPath">Absolute path the file would be imported to in the library.</param>
    /// <param name="IsConfident">False when a fallback TMDB search strategy was used - highlighted in the grid.</param>
    /// <param name="IsMatched">False when no TMDB result was found - highlighted in the grid; ProposedPath is empty.</param>
    public sealed record MediaProposal(string MediaType, string OriginalPath, string ProposedPath, bool IsConfident = true, bool IsMatched = true)
    {
        public const string TypeMovie  = "Movie";
        public const string TypeTvShow = "TV Show";
    }
}
