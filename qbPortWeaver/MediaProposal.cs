namespace qbPortWeaver
{
    /// <summary>Represents a proposed media file import - the original file path and the Plex-compliant target path in the library.</summary>
    /// <param name="MediaType">Short label used in the results grid, e.g. <see cref="TypeMovie"/> or <see cref="TypeTvShow"/>.</param>
    /// <param name="OriginalPath">Absolute path to the existing file in the source folder.</param>
    /// <param name="ProposedPath">Absolute path the file would be imported to in the library.</param>
    /// <param name="IsConfident">False when a fallback TMDB search strategy was used - highlighted in the grid.</param>
    /// <param name="IsMatched">False when no TMDB result was found - highlighted in the grid; ProposedPath is empty.</param>
    /// <param name="PosterPath">TMDB poster path (e.g. <c>/abc123.jpg</c>), used to fetch the thumbnail image.</param>
    /// <param name="TmdbId">TMDB numeric ID for the matched title.</param>
    /// <param name="VoteCount">TMDB vote count for the matched title, used as a match quality signal in the detail panel.</param>
    public sealed record MediaProposal(string MediaType, string OriginalPath, string ProposedPath, bool IsConfident = true, bool IsMatched = true, string? PosterPath = null, int? TmdbId = null, int VoteCount = 0)
    {
        public const string TypeMovie  = "Movie";
        public const string TypeTvShow = "TV Show";
    }
}
