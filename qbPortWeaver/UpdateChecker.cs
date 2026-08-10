using System.Net.Http.Headers;
using System.Text.Json;

namespace qbPortWeaver;

/// <summary>Latest release metadata from GitHub, including whether it is newer than the running version.</summary>
/// <param name="TagName">Git tag name (e.g. "v2.1.0").</param>
/// <param name="ReleaseUrl">URL of the GitHub release page.</param>
/// <param name="IsNewer">True when the release version is greater than <see cref="AppConstants.AppVersion"/>.</param>
/// <param name="MsiUrl">Direct download URL of the release's .msi installer asset, or <see langword="null"/> if the release has none.</param>
public sealed record LatestReleaseInfo(string TagName, string ReleaseUrl, bool IsNewer, string? MsiUrl = null)
{
    /// <summary>Tag name with the leading 'v'/'V' stripped (e.g. "v2.1.0" becomes "2.1.0").</summary>
    public string Version => TagName.TrimStart('v', 'V');
}

/// <summary>A GitHub contributor's login and profile URL.</summary>
/// <param name="Login">GitHub username.</param>
/// <param name="ProfileUrl">URL of the contributor's GitHub profile.</param>
public sealed record ContributorInfo(string Login, string ProfileUrl);

/// <summary>Queries the GitHub API for release and contributor information.</summary>
public static class UpdateChecker
{
    private const string JsonPropTagName = "tag_name";
    private const string JsonPropHtmlUrl = "html_url";
    private const string JsonPropAssets = "assets";
    private const string JsonPropAssetName = "name";
    private const string JsonPropAssetDownloadUrl = "browser_download_url";

    private static readonly string _gitHubBaseApiUrl = $"https://api.github.com/repos/{AppConstants.GitHubRepoOwner}/{AppIdentity.AppName}";
    private static readonly string _gitHubApiUrl = _gitHubBaseApiUrl + "/releases/latest";

    private static readonly HttpClient _httpClient = CreateHttpClient(); // Not disposed - static lifetime matches process lifetime (recommended pattern for HttpClient)
    // Separate client for asset downloads: no timeout (an installer download is far larger and slower
    // than an API call - the caller's CancellationToken bounds it instead). Static, process-lifetime.
    private static readonly HttpClient _downloadClient = CreateDownloadClient();

    /// <summary>Returns full release info from GitHub including whether a newer version exists; null on any error.</summary>
    public static async Task<LatestReleaseInfo?> GetLatestReleaseInfoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(_gitHubApiUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;

            if (root.GetStringOrNull(JsonPropTagName) is not { } tagName ||
                root.GetStringOrNull(JsonPropHtmlUrl) is not { } releaseUrl)
                return null;

            string versionStr = tagName.TrimStart('v', 'V');

            bool isNewer = Version.TryParse(versionStr, out var latest) &&
                           Version.TryParse(AppConstants.AppVersion, out var current) &&
                           latest > current;

            return new LatestReleaseInfo(tagName, releaseUrl, isNewer, TryGetMsiAssetUrl(root));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null; // shutdown - not a real failure, suppress log noise
        }
        catch (Exception ex)
        {
            // No OCE filter here (matches GetReleaseContributorsAsync): the arm above already
            // suppresses shutdown cancellation, so anything reaching here - including a non-token
            // HttpClient timeout surfacing as TaskCanceledException - is a real failure to log,
            // not propagate.
            LogManager.Instance.LogDebug($"UpdateChecker.GetLatestReleaseInfoAsync: {ex.Message}");
            return null;
        }
    }

    /// <summary>Returns all unique human contributors to the repo, with the owner listed first. Bots are excluded. Returns an empty list on any error.</summary>
    public static async Task<IReadOnlyList<ContributorInfo>> GetReleaseContributorsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(_gitHubBaseApiUrl + "/contributors?per_page=100", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var contributors = new List<ContributorInfo>();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var contributor = TryParseContributor(item);
                if (contributor is not null && seen.Add(contributor.Login))
                    contributors.Add(contributor);
            }

            MoveOwnerToFront(contributors);
            return contributors;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return []; // shutdown - not a real failure, suppress log noise
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"UpdateChecker.GetReleaseContributorsAsync: {ex.Message}");
            return [];
        }
    }

    // Parses a single contributor object from the GitHub API response, returning null when the entry
    // is missing a login, is a bot, or cannot be interpreted as a human contributor.
    private static ContributorInfo? TryParseContributor(JsonElement item)
    {
        string login = item.GetStringOrNull("login") ?? string.Empty;
        if (string.IsNullOrEmpty(login)) return null;

        string type = item.GetStringOrNull("type") ?? string.Empty;
        if (IsBot(login, type)) return null;

        string url = item.GetStringOrNull(JsonPropHtmlUrl) ?? string.Empty;
        return new ContributorInfo(login, url);
    }

    // Ensures the repo owner is the first entry when present anywhere else in the list.
    private static void MoveOwnerToFront(List<ContributorInfo> contributors)
    {
        int ownerIndex = contributors.FindIndex(c => c.Login.Equals(AppConstants.GitHubRepoOwner, StringComparison.OrdinalIgnoreCase));
        if (ownerIndex > 0)
        {
            var owner = contributors[ownerIndex];
            contributors.RemoveAt(ownerIndex);
            contributors.Insert(0, owner);
        }
    }

    // Creates the shared HttpClient pre-configured with the required User-Agent, timeout,
    // and GitHub API headers (Accept media type and pinned API version) so future GitHub
    // default changes do not silently alter the response shape.
    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(AppConstants.HttpTimeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.AppName, AppConstants.AppVersion));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    // Download client sends the required GitHub User-Agent but no timeout (see field comment).
    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.AppName, AppConstants.AppVersion));
        return client;
    }

    // Returns the download URL of the release's first .msi asset, or null when the release has none
    // (e.g. assets not yet uploaded). Callers fall back to opening the release page.
    private static string? TryGetMsiAssetUrl(JsonElement root)
    {
        if (!root.TryGetProperty(JsonPropAssets, out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assetsElement.EnumerateArray())
        {
            string name = asset.GetStringOrNull(JsonPropAssetName) ?? string.Empty;
            if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) &&
                asset.GetStringOrNull(JsonPropAssetDownloadUrl) is { Length: > 0 } url)
                return url;
        }
        return null;
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destPath"/> (overwriting it), reporting
    /// fractional progress (0.0-1.0) when the response length is known. Throws
    /// <see cref="OperationCanceledException"/> on cancellation and <see cref="HttpRequestException"/>
    /// or <see cref="IOException"/> on a transfer failure. The caller's token bounds the transfer time.
    /// </summary>
    public static async Task DownloadFileAsync(string url, string destPath, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        using var response = await _downloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            if (total is > 0)
                progress?.Report((double)received / total.Value);
        }
    }

    private static bool IsBot(string login, string type) =>
        type.Equals("Bot", StringComparison.OrdinalIgnoreCase) ||
        login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);
}
