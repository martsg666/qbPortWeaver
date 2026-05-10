using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace qbPortWeaver
{
    /// <summary>HTTP client for The Movie Database (TMDB) search API. Applies a per-request delay to stay within TMDB's rate limit (~40 requests/10 seconds).</summary>
    public sealed class TmdbClient(string apiKey)
    {
        private const string TmdbBaseUrl = "https://api.themoviedb.org/3/"; // NOSONAR S1075 - fixed TMDB API endpoint, not a configurable path
        private const int    RateLimitDelayMs = 260; // ~3.8 req/s, comfortably under TMDB's ~4 req/s limit

        // Static shared instance: HttpClient is thread-safe and reusing it avoids per-cycle socket exhaustion.
        private static readonly HttpClient _httpClient = new()
        {
            BaseAddress = new Uri(TmdbBaseUrl),
            Timeout     = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds)
        };

        // Serialises concurrent requests to enforce the rate limit
        private static readonly SemaphoreSlim _rateLimiter = new(1, 1);

        private readonly bool _useBearer = apiKey.StartsWith("eyJ", StringComparison.Ordinal)
                                           && apiKey.IndexOf('.') != apiKey.LastIndexOf('.');

        private const string TmdbImageBaseUrl = "https://image.tmdb.org/t/p/w92"; // NOSONAR S1075 - fixed TMDB image CDN base, not a configurable path

        private static readonly HttpClient _imageHttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds)
        };

        /// <summary>Returns all TMDB movie candidates for a query in relevance order, or null if none found.</summary>
        internal async Task<IReadOnlyList<MovieInfo>?> SearchMovieCandidatesAsync(string query, int? year = null, CancellationToken cancellationToken = default)
        {
            var url = _useBearer // NOSONAR S4790 - key transmitted over HTTPS only; v3 in query param, v4 in Authorization header
                ? $"search/movie?query={Uri.EscapeDataString(query)}&language=en-US&page=1"
                : $"search/movie?api_key={apiKey}&query={Uri.EscapeDataString(query)}&language=en-US&page=1";
            if (year.HasValue)
                url += $"&year={year.Value}";
            var response = await GetWithRateLimitAsync<TmdbMovieSearchResult>(url, cancellationToken).ConfigureAwait(false);
            var results  = response?.Results;
            if (results is null or { Count: 0 }) return null;
            return results.ConvertAll(r => new MovieInfo(r.Title, ParseYearFromDate(r.ReleaseDate), r.Id, r.VoteCount, r.PosterPath, r.Overview));
        }

        /// <summary>Returns all TMDB TV show candidates for a query in relevance order, or null if none found.</summary>
        internal async Task<IReadOnlyList<TvShowInfo>?> SearchTvShowCandidatesAsync(string query, int? year = null, CancellationToken cancellationToken = default)
        {
            var url = _useBearer // NOSONAR S4790 - key transmitted over HTTPS only; v3 in query param, v4 in Authorization header
                ? $"search/tv?query={Uri.EscapeDataString(query)}&language=en-US&page=1"
                : $"search/tv?api_key={apiKey}&query={Uri.EscapeDataString(query)}&language=en-US&page=1";
            if (year.HasValue)
                url += $"&first_air_date_year={year.Value}";
            var response = await GetWithRateLimitAsync<TmdbTvSearchResult>(url, cancellationToken).ConfigureAwait(false);
            var results  = response?.Results;
            if (results is null or { Count: 0 }) return null;
            return results.ConvertAll(r => new TvShowInfo(r.Name, ParseYearFromDate(r.FirstAirDate), r.Id, r.VoteCount, r.PosterPath, r.Overview));
        }

        /// <summary>Downloads a TMDB poster image by its path. Returns null on failure or cancellation.</summary>
        internal static async Task<Image?> FetchPosterAsync(string posterPath, CancellationToken cancellationToken)
        {
            try
            {
                var bytes = await _imageHttpClient.GetByteArrayAsync($"{TmdbImageBaseUrl}{posterPath}", cancellationToken).ConfigureAwait(false);
                using var ms  = new MemoryStream(bytes);
                using var src = Image.FromStream(ms);
                return new Bitmap(src); // copy to break stream dependency
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ArgumentException)
            {
                return null;
            }
        }

        // Extracts the year from a TMDB date string (format "YYYY-MM-DD"), or null if the string is missing or malformed.
        private static int? ParseYearFromDate(string? date) =>
            date?.Length >= 4 && int.TryParse(date[..4], out int y) ? y : null;

        /// <summary>
        /// Searches TMDB with confidence tracking: primary search, no-year confidence check,
        /// retry without year, and fallback strategies (after-dash, trailing-number).
        /// Shared by both processors and the re-match UI to avoid duplicating lookup logic.
        /// </summary>
        internal static async Task<(T? Info, bool IsConfident)> SearchWithConfidenceAsync<T>(
            string title, int? year,
            Func<string, int?, Task<IReadOnlyList<T>?>> search,
            Func<T, bool> hasYear,
            Func<T, string> getTitle,
            Func<T, int> getVoteCount) where T : class
        {
            bool isConfident = true;

            var candidates = await search(title, year).ConfigureAwait(false);

            // Prefer an exact normalized title match over TMDB's top-ranked result.
            // Among exact matches, prefer one that also has a year (tiebreaker) but do not
            // exclude a yearless exact match - year is corroborating evidence, not a hard gate.
            // Scanning the full candidates list avoids accepting a longer near-miss as the best match.
            var normalizedSearch = FileNameParser.NormalizeTitleForMatch(title);
            var searchedWords    = normalizedSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var info = candidates is not null
                ? (candidates.FirstOrDefault(c => hasYear(c) &&
                       string.Equals(FileNameParser.NormalizeTitleForMatch(getTitle(c)), normalizedSearch, StringComparison.Ordinal))
                   ?? candidates.FirstOrDefault(c =>
                       string.Equals(FileNameParser.NormalizeTitleForMatch(getTitle(c)), normalizedSearch, StringComparison.Ordinal))
                   ?? candidates[0])
                : null;

            // Without a year in the filename we cannot corroborate the match by year alone.
            // Require an exact title match and a meaningful vote count to stay confident.
            if (info is not null && !year.HasValue)
                isConfident = IsStrongNoYearMatch(normalizedSearch, getTitle(info), getVoteCount(info));

            // With a year present, a short searched title can still match a longer TMDB title.
            // Mark uncertain when all searched-title words appear in the returned title's word set
            // and the returned title has strictly more words (word-subset match).
            else if (info is not null)
                isConfident = !IsWordSubsetMatch(searchedWords, getTitle(info));

            // Retry without year: parsed year may not match TMDB's release/first-air year
            if (info is null && year.HasValue)
            {
                candidates = await search(title, null).ConfigureAwait(false);
                info       = candidates?[0];
                if (info is not null) isConfident = false;
            }

            (info, isConfident) = await TryFallbackLookupsAsync(
                title, year, info, isConfident, search, hasYear).ConfigureAwait(false);

            return (info, isConfident);
        }

        /// <summary>
        /// Performs a TMDB lookup with confidence tracking, handling "no match" logging and HTTP/timeout errors.
        /// Shared by both processors so the error handling and log format live in one place.
        /// </summary>
        internal static async Task<(T? Info, bool IsConfident)> LookupAsync<T>(
            (string Title, int? Year) query,
            Func<string, int?, Task<IReadOnlyList<T>?>> search,
            Func<T, bool> hasYear,
            Func<T, string> getTitle,
            Func<T, int> getVoteCount,
            string mediaKind,
            CancellationToken cancellationToken = default) where T : class
        {
            try
            {
                var (info, isConfident) = await SearchWithConfidenceAsync(
                    query.Title, query.Year, search, hasYear, getTitle, getVoteCount).ConfigureAwait(false);

                if (info is null)
                {
                    LogManager.Instance.LogMessage($"No TMDB match found for {mediaKind} '{query.Title}'", LogLevel.Warn, Subsystem.MediaManager);
                    return (null, false);
                }

                return (info, isConfident);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                LogManager.Instance.LogMessage($"Failed to look up TMDB {mediaKind}: {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
                return (null, false);
            }
        }

        // Enforces a minimum delay between TMDB API calls to avoid HTTP 429 rate limiting.
        // The delay runs inside the semaphore hold so the next caller waits for the cooldown to finish.
        private async Task<T?> GetWithRateLimitAsync<T>(string url, CancellationToken cancellationToken = default)
        {
            await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_useBearer)
                {
                    using var request  = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                    using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
                }
                return await _httpClient.GetFromJsonAsync<T>(url, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Delay inside the semaphore hold so the next caller waits for the cooldown.
                // CancellationToken.None: cooldown is 260ms and must not be skipped by a mid-call cancellation.
                // Inner finally ensures Release() runs even if Task.Delay somehow throws.
                try { await Task.Delay(RateLimitDelayMs, CancellationToken.None).ConfigureAwait(false); }
                finally { _rateLimiter.Release(); }
            }
        }

        // Returns true when a TMDB result found without a year in the filename is a high-confidence match.
        // Requires a meaningful vote count and a normalised title match to filter out obscure or incorrect entries.
        private static bool IsStrongNoYearMatch(string normalizedSearchTitle, string returnedTitle, int voteCount, int minVoteCount = 50) =>
            voteCount >= minVoteCount &&
            string.Equals(FileNameParser.NormalizeTitleForMatch(returnedTitle), normalizedSearchTitle, StringComparison.Ordinal);

        // Returns true when all words of the searched title appear in the returned title's word set
        // and the returned title has strictly more distinct words - i.e. the searched title is a proper word subset.
        // Both sides use distinct word sets so repeated words (e.g. "The The") don't skew the count.
        private static bool IsWordSubsetMatch(string[] normalizedSearchedWords, string returnedTitle)
        {
            var returnedWords = new HashSet<string>(
                FileNameParser.NormalizeTitleForMatch(returnedTitle)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);
            var searchedWordSet = new HashSet<string>(normalizedSearchedWords, StringComparer.Ordinal);
            return returnedWords.Count > searchedWordSet.Count &&
                   searchedWordSet.All(w => returnedWords.Contains(w));
        }

        // Applies two fallback lookup strategies when the primary search fails or returns no match.
        // After-dash strategy: retries with the part after " - " when info is null.
        // Trailing-number strategy: retries without a trailing digit when info is null OR the result has no year
        // (a year-less result is ambiguous; a stripped-title result that includes a year is higher quality).
        private static async Task<(T? Info, bool IsConfident)> TryFallbackLookupsAsync<T>(
            string title, int? year, T? info, bool isConfident,
            Func<string, int?, Task<IReadOnlyList<T>?>> search,
            Func<T, bool> hasYear) where T : class
        {
            var afterDash = info is null ? ExtractAfterDash(title) : null;
            if (afterDash is not null)
            {
                LogManager.Instance.LogDebug($"TmdbClient.TryFallbackLookupsAsync: Retrying with after-dash title '{afterDash}'", Subsystem.MediaManager);
                var afterDashInfo = (await search(afterDash, year).ConfigureAwait(false))?[0];
                if (afterDashInfo is not null)
                {
                    info        = afterDashInfo;
                    isConfident = false;
                }
            }

            if (info is null || (!hasYear(info) && !isConfident))
            {
                var withoutNum = StripTrailingNumber(title);
                if (withoutNum is not null)
                {
                    LogManager.Instance.LogDebug($"TmdbClient.TryFallbackLookupsAsync: Retrying without trailing number '{withoutNum}'", Subsystem.MediaManager);
                    var withoutNumInfo = (await search(withoutNum, year).ConfigureAwait(false))?[0];
                    if (withoutNumInfo is not null && hasYear(withoutNumInfo))
                    {
                        info        = withoutNumInfo;
                        isConfident = false;
                    }
                }
            }

            return (info, isConfident);
        }

        // Returns the substring after the first " - " separator, or null if not present.
        private static string? ExtractAfterDash(string title)
        {
            int idx = title.IndexOf(" - ", StringComparison.Ordinal);
            if (idx < 0) return null;
            var after = title[(idx + 3)..].Trim();
            return after.Length > 0 ? after : null;
        }

        // Strips a single trailing digit preceded by a space (e.g. "Title 2" -> "Title"), or null if not present.
        private static string? StripTrailingNumber(string title)
        {
            var trimmed = title.TrimEnd();
            if (trimmed.Length <= 2 || !char.IsDigit(trimmed[^1]) || trimmed[^2] != ' ')
                return null;
            return trimmed[..^2].Trim();
        }
    }

    /// <summary>TMDB title, release year, database ID, vote count, poster path, and overview for a movie.</summary>
    public sealed record MovieInfo(string Title, int? Year, int TmdbId, int VoteCount = 0, string? PosterPath = null, string? Overview = null);

    /// <summary>TMDB title, first-air year, database ID, vote count, poster path, and overview for a TV show.</summary>
    public sealed record TvShowInfo(string Title, int? Year, int TmdbId, int VoteCount = 0, string? PosterPath = null, string? Overview = null);

    // TMDB API response shapes - only used for deserialization
    internal sealed record TmdbMovieSearchResult(
        [property: JsonPropertyName("results")] List<TmdbMovie>? Results);

    internal sealed record TmdbMovie(
        [property: JsonPropertyName("id")]           int     Id,
        [property: JsonPropertyName("title")]        string  Title,
        [property: JsonPropertyName("release_date")] string? ReleaseDate,
        [property: JsonPropertyName("vote_count")]   int     VoteCount    = 0,
        [property: JsonPropertyName("poster_path")]  string? PosterPath   = null,
        [property: JsonPropertyName("overview")]     string? Overview     = null);

    internal sealed record TmdbTvSearchResult(
        [property: JsonPropertyName("results")] List<TmdbTvShow>? Results);

    internal sealed record TmdbTvShow(
        [property: JsonPropertyName("id")]             int     Id,
        [property: JsonPropertyName("name")]           string  Name,
        [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
        [property: JsonPropertyName("vote_count")]     int     VoteCount    = 0,
        [property: JsonPropertyName("poster_path")]    string? PosterPath   = null,
        [property: JsonPropertyName("overview")]       string? Overview     = null);
}
