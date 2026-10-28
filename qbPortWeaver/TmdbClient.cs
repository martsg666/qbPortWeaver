using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace qbPortWeaver;

/// <summary>HTTP client for The Movie Database (TMDB) search API. Applies a per-request delay to stay within TMDB's rate limit (~40 requests/10 seconds).</summary>
public sealed class TmdbClient(string apiKey)
{
    private const string TmdbBaseUrl = "https://api.themoviedb.org/3/"; // NOSONAR S1075 - fixed TMDB API endpoint, not a configurable path
    private const int RateLimitDelayMs = 260; // ~3.8 req/s, comfortably under TMDB's ~4 req/s limit

    // Static shared instance: HttpClient is thread-safe and reusing it avoids per-cycle socket exhaustion.
    private static readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri(TmdbBaseUrl),
        Timeout = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds)
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
        var results = response?.Results;
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
        var results = response?.Results;
        if (results is null or { Count: 0 }) return null;
        return results.ConvertAll(r => new TvShowInfo(r.Name, ParseYearFromDate(r.FirstAirDate), r.Id, r.VoteCount, r.PosterPath, r.Overview));
    }

    /// <summary>Downloads a TMDB poster image by its path. Returns null on failure or cancellation.</summary>
    internal static async Task<Image?> FetchPosterAsync(string posterPath, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _imageHttpClient.GetByteArrayAsync($"{TmdbImageBaseUrl}{posterPath}", cancellationToken).ConfigureAwait(false);
            using var ms = new MemoryStream(bytes);
            using var src = Image.FromStream(ms);
            return new Bitmap(src); // copy to break stream dependency
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        // OutOfMemoryException is GDI+ signalling invalid image data, not memory exhaustion:
        // Image.FromStream throws it for anything it cannot decode, which is what a captive portal
        // or a truncated download produces on an otherwise successful response. Letting it escape
        // kills the app, because the caller is an async void event handler that only expects
        // OperationCanceledException. Returning null is the documented "no poster" outcome.
        // IOException: GetByteArrayAsync streams the body, so a connection reset mid-download surfaces as
        // an HttpIOException rather than an HttpRequestException.
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
                                         or ArgumentException or OutOfMemoryException or NotSupportedException)
        {
            LogManager.Instance.LogDebug(
                $"TmdbClient.FetchPosterAsync: Could not decode poster ({ex.GetType().Name})", Subsystem.MediaManager);
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
        Func<string, int?, CancellationToken, Task<IReadOnlyList<T>?>> search,
        Func<T, bool> hasYear,
        Func<T, string> getTitle,
        Func<T, int> getVoteCount,
        CancellationToken cancellationToken = default) where T : class
    {
        bool isConfident = true;

        var candidates = await search(title, year, cancellationToken).ConfigureAwait(false);

        // Prefer an exact normalized title match over TMDB's top-ranked result.
        // Among exact matches, prefer one that also has a year (tiebreaker) but do not
        // exclude a yearless exact match - year is corroborating evidence, not a hard gate.
        // Scanning the full candidates list avoids accepting a longer near-miss as the best match.
        var normalizedSearch = FileNameParser.NormalizeTitleForMatch(title);
        var searchedWords = normalizedSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var info = candidates is not null
            ? (candidates.FirstOrDefault(c => hasYear(c) &&
                   string.Equals(FileNameParser.NormalizeTitleForMatch(getTitle(c)), normalizedSearch, StringComparison.Ordinal))
               ?? candidates.FirstOrDefault(c =>
                   string.Equals(FileNameParser.NormalizeTitleForMatch(getTitle(c)), normalizedSearch, StringComparison.Ordinal))
               // TMDB's own top-ranked result, read through FirstOrNull rather than indexed: the
               // ternary guards null but not empty, and this method takes an arbitrary search
               // delegate, so "never returns an empty list" is a property of today's two callers
               // rather than of anything enforced here. An empty list now falls through to the
               // retry-without-year branch below instead of throwing past both callers' catch
               // filters. Same accessor the retry branch and the fallback lookups already use.
               ?? FirstOrNull(candidates))
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
            candidates = await search(title, null, cancellationToken).ConfigureAwait(false);
            info = FirstOrNull(candidates);
            if (info is not null) isConfident = false;
        }

        (info, isConfident) = await TryFallbackLookupsAsync(
            title, year, info, isConfident, search, hasYear, cancellationToken).ConfigureAwait(false);

        return (info, isConfident);
    }

    /// <summary>
    /// Performs a TMDB lookup with confidence tracking, handling "no match" logging and HTTP/timeout errors.
    /// Shared by both processors so the error handling and log format live in one place.
    /// </summary>
    internal static async Task<(T? Info, bool IsConfident)> LookupAsync<T>(
        (string Title, int? Year) query,
        Func<string, int?, CancellationToken, Task<IReadOnlyList<T>?>> search,
        Func<T, bool> hasYear,
        Func<T, string> getTitle,
        Func<T, int> getVoteCount,
        string mediaKind,
        CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            var (info, isConfident) = await SearchWithConfidenceAsync(
                query.Title, query.Year, search, hasYear, getTitle, getVoteCount, cancellationToken).ConfigureAwait(false);

            if (info is null)
            {
                LogManager.Instance.LogMessage($"No TMDB match found for {mediaKind} '{query.Title}'", LogLevel.Warn, Subsystem.MediaManager);
                return (null, false);
            }

            return (info, isConfident);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TmdbUnavailableException ex)
        {
            ReportUnavailable($"{mediaKind} '{query.Title}'", ex);
            return (null, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogManager.Instance.LogMessage($"Failed to look up TMDB {mediaKind}: {ex.Message}", LogLevel.Warn, Subsystem.MediaManager);
            return (null, false);
        }
    }

    // State key for the "TMDB unavailable" Warn, so a run of failures reports once.
    private const string UnavailableStateKey = "tmdb.unavailable";

    /// <summary>
    /// Reports a lookup skipped because TMDB is unavailable: the title at Debug, and once per state the Warn
    /// that says why - shared by the import and scan lookups and by the window's Re-match, so a run of
    /// failures says it once however many titles it skips. Cleared by the next successful request.
    /// </summary>
    internal static void ReportUnavailable(string what, Exception ex)
    {
        LogManager.Instance.LogDebug($"TmdbClient.ReportUnavailable: {what} skipped: {ex.Message}", Subsystem.MediaManager);
        LogManager.Instance.LogStateChange(UnavailableStateKey,
            "TMDB is unreachable or rejected the API key - the remaining lookups of this run are skipped; check the API key in the Media Manager and the connection",
            LogLevel.Warn, Subsystem.MediaManager);
    }

    // Thrown for a lookup once TMDB has failed in a way every later lookup of the run would repeat: the
    // key was rejected (401/403), or TMDB could not be reached or timed out twice in a row. Each later
    // call then fails at once instead of spending a request - up to the 10 s timeout - and a Warn per title.
    // Public only because exception types must be (S3871).
    public sealed class TmdbUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

    // Set by the first such failure; this instance serves one import, scan or Re-match run, so it resets
    // with the next.
    private volatile bool _unavailable;

    /// <summary>Whether TMDB became unavailable during this instance's run, so lookups after that point
    /// were skipped rather than answered.</summary>
    internal bool WasUnavailable => _unavailable;

    // Consecutive transport failures: timeouts, and requests that got no HTTP answer at all. One alone is a
    // slow answer or a dropped connection (a VPN reconnect resets it mid-run), not an outage - it costs that
    // one title - so the run is only given up on at the second in a row. Reset by any successful request.
    private int _consecutiveTransportFailures;
    private const int TransportFailuresBeforeUnavailable = 2;

    // Whether a failure means every later lookup of the run would fail the same way. Counts transport
    // failures as a side effect; called from an exception filter, which runs inside the limiter hold, so
    // the count is serialised with the request that failed.
    private bool MeansUnavailable(Exception ex, CancellationToken cancellationToken)
    {
        switch (ex)
        {
            case HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }:
                return true;
            case HttpRequestException { StatusCode: null }:                                 // could not be reached
            case TaskCanceledException when !cancellationToken.IsCancellationRequested:     // timed out
                return Interlocked.Increment(ref _consecutiveTransportFailures) >= TransportFailuresBeforeUnavailable;
            default:
                return false;
        }
    }

    // Enforces a minimum delay between TMDB API calls to avoid HTTP 429 rate limiting.
    // The delay runs inside the semaphore hold so the next caller waits for the cooldown to finish.
    private async Task<T?> GetWithRateLimitAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        if (_unavailable) throw new TmdbUnavailableException("TMDB failed earlier in this run");
        await _rateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Again once it is this caller's turn: callers already queued on the limiter when the failure came
        // would otherwise each still send a request, at up to the timeout apiece. Released without the
        // cooldown, which only spaces requests that are actually sent.
        if (_unavailable)
        {
            _rateLimiter.Release();
            throw new TmdbUnavailableException("TMDB failed earlier in this run");
        }
        try
        {
            // One request path for both key formats. SendAsync buffers the whole body before returning, so a
            // connection reset mid-reply surfaces as an HttpRequestException with no status, which
            // MeansUnavailable counts; GetFromJsonAsync streamed it instead, and the same reset escaped as an
            // HttpIOException that no TMDB handler caught, depending only on which key format was pasted.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (_useBearer)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            T? result = await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _consecutiveTransportFailures, 0);
            LogManager.Instance.ClearLogState(UnavailableStateKey);
            return result;
        }
        catch (Exception ex) when (MeansUnavailable(ex, cancellationToken))
        {
            _unavailable = true;
            throw new TmdbUnavailableException(ex.Message, ex);
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
        Func<string, int?, CancellationToken, Task<IReadOnlyList<T>?>> search,
        Func<T, bool> hasYear,
        CancellationToken cancellationToken = default) where T : class
    {
        (info, isConfident) = await TryAfterDashLookupAsync(title, year, info, isConfident, search, cancellationToken).ConfigureAwait(false);

        if (info is null || (!hasYear(info) && !isConfident))
            (info, isConfident) = await TryTrailingNumberLookupAsync(title, year, info, isConfident, search, hasYear, cancellationToken).ConfigureAwait(false);

        return (info, isConfident);
    }

    // After-dash strategy: retries with the part after " - " when info is null. No-op if info is
    // already set or the title has no " - " separator.
    private static async Task<(T? Info, bool IsConfident)> TryAfterDashLookupAsync<T>(
        string title, int? year, T? info, bool isConfident,
        Func<string, int?, CancellationToken, Task<IReadOnlyList<T>?>> search,
        CancellationToken cancellationToken) where T : class
    {
        if (info is not null) return (info, isConfident);

        var afterDash = ExtractAfterDash(title);
        if (afterDash is null) return (info, isConfident);

        LogManager.Instance.LogDebug($"TmdbClient.TryFallbackLookupsAsync: Retrying with after-dash title '{afterDash}'", Subsystem.MediaManager);
        var results = await search(afterDash, year, cancellationToken).ConfigureAwait(false);
        var candidate = FirstOrNull(results);
        return candidate is not null ? (candidate, false) : (info, isConfident);
    }

    // Trailing-number strategy: retries without a trailing digit. Only called when info is null OR
    // the result has no year (a year-less result is ambiguous; a stripped-title result that includes
    // a year is higher quality).
    private static async Task<(T? Info, bool IsConfident)> TryTrailingNumberLookupAsync<T>(
        string title, int? year, T? info, bool isConfident,
        Func<string, int?, CancellationToken, Task<IReadOnlyList<T>?>> search,
        Func<T, bool> hasYear,
        CancellationToken cancellationToken) where T : class
    {
        var withoutNum = StripTrailingNumber(title);
        if (withoutNum is null) return (info, isConfident);

        LogManager.Instance.LogDebug($"TmdbClient.TryFallbackLookupsAsync: Retrying without trailing number '{withoutNum}'", Subsystem.MediaManager);
        var results = await search(withoutNum, year, cancellationToken).ConfigureAwait(false);
        var candidate = FirstOrNull(results);
        return candidate is not null && hasYear(candidate) ? (candidate, false) : (info, isConfident);
    }

    private static T? FirstOrNull<T>(IReadOnlyList<T>? results) where T : class =>
        results is { Count: > 0 } ? results[0] : null;

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
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("release_date")] string? ReleaseDate,
    [property: JsonPropertyName("vote_count")] int VoteCount = 0,
    [property: JsonPropertyName("poster_path")] string? PosterPath = null,
    [property: JsonPropertyName("overview")] string? Overview = null);

internal sealed record TmdbTvSearchResult(
    [property: JsonPropertyName("results")] List<TmdbTvShow>? Results);

internal sealed record TmdbTvShow(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("first_air_date")] string? FirstAirDate,
    [property: JsonPropertyName("vote_count")] int VoteCount = 0,
    [property: JsonPropertyName("poster_path")] string? PosterPath = null,
    [property: JsonPropertyName("overview")] string? Overview = null);
