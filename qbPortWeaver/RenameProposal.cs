namespace qbPortWeaver
{
    /// <summary>Represents a proposed rename - the original file path and the full target path after applying Plex naming conventions.</summary>
    /// <param name="MediaType">Short label used in the results grid, e.g. "Movie" or "TV Show".</param>
    /// <param name="OriginalPath">Absolute path to the existing file or folder.</param>
    /// <param name="ProposedPath">Absolute path the item would be moved/renamed to.</param>
    /// <param name="IsConfident">False when a fallback TMDB search strategy was used - shown in red in the grid.</param>
    /// <param name="IsMatched">False when no TMDB result was found - shown in orange in the grid; ProposedPath is empty.</param>
    public sealed record RenameProposal(string MediaType, string OriginalPath, string ProposedPath, bool IsConfident = true, bool IsMatched = true);
}
