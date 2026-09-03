using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace qbPortWeaver;

/// <summary>Latest release metadata from GitHub, including whether it is newer than the running version.</summary>
/// <param name="TagName">Git tag name (e.g. "vX.Y.Z").</param>
/// <param name="ReleaseUrl">URL of the GitHub release page.</param>
/// <param name="IsNewer">True when the release version is greater than <see cref="AppConstants.AppVersion"/>.</param>
/// <param name="MsiUrl">Direct download URL of the release's .msi installer asset, or <see langword="null"/> if the release has none.</param>
/// <param name="MsiSha256">
/// SHA-256 the GitHub API reports for that asset, lower-case hex with the <c>sha256:</c> prefix
/// stripped, or <see langword="null"/> when the API did not report one.
/// <para>Verified before the installer is launched - see <see cref="UpdateChecker.DownloadFileAsync"/>.
/// Null is treated as "cannot verify, proceed", the same way a helper response with no <c>v=</c> key
/// means an older peer rather than a failure: the field is a relatively recent GitHub addition, and
/// refusing to update without it would break the path it exists to protect.</para>
/// </param>
public sealed record LatestReleaseInfo(string TagName, string ReleaseUrl, bool IsNewer, string? MsiUrl = null, string? MsiSha256 = null)
{
    /// <summary>Tag name with the leading 'v'/'V' stripped (e.g. "vX.Y.Z" becomes "X.Y.Z").</summary>
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
    private const string JsonPropAssetDigest = "digest";
    private const string DigestSha256Prefix = "sha256:";

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

            var (msiUrl, msiSha256) = TryGetMsiAsset(root);
            return new LatestReleaseInfo(tagName, releaseUrl, isNewer, msiUrl, msiSha256);
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

    // Returns the download URL of the release's first .msi asset and the SHA-256 the API reports for
    // it, or (null, null) when the release has no .msi (e.g. assets not yet uploaded). Callers fall
    // back to opening the release page.
    //
    // The digest is read from the same asset object as the URL, so the hash and the bytes it applies
    // to cannot come from different responses. GitHub reports it as "sha256:<64 hex>"; anything not
    // in that shape is discarded rather than guessed at, which degrades to the unverified path.
    private static (string? Url, string? Sha256) TryGetMsiAsset(JsonElement root)
    {
        if (!root.TryGetProperty(JsonPropAssets, out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
            return (null, null);

        foreach (var asset in assetsElement.EnumerateArray())
        {
            string name = asset.GetStringOrNull(JsonPropAssetName) ?? string.Empty;
            if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) &&
                asset.GetStringOrNull(JsonPropAssetDownloadUrl) is { Length: > 0 } url)
                return (url, ParseSha256Digest(asset.GetStringOrNull(JsonPropAssetDigest)));
        }
        return (null, null);
    }

    // "sha256:<64 hex>" -> the hex alone; null for absent, a different algorithm, or a malformed value.
    private static string? ParseSha256Digest(string? digest)
    {
        if (digest is null || !digest.StartsWith(DigestSha256Prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string hex = digest[DigestSha256Prefix.Length..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destPath"/> (overwriting it), reporting
    /// fractional progress (0.0-1.0) when the response length is known. The caller's token bounds the
    /// transfer time.
    /// <para>Throws <see cref="OperationCanceledException"/> on cancellation,
    /// <see cref="HttpRequestException"/> or <see cref="IOException"/> on a transfer failure, and
    /// <see cref="InvalidDataException"/> when <paramref name="expectedSha256"/> is supplied and does
    /// not match. That last one is <b>not</b> an <see cref="IOException"/> - it derives from
    /// <see cref="SystemException"/> - so a caller that filters on the two transfer types alone would
    /// let a checksum failure escape. Catch it explicitly, as
    /// <c>UpdateAvailableForm.DownloadAndInstallAsync</c> does.</para>
    /// <para>The destination file is removed on every one of those paths, so a failure never leaves a
    /// partial or unverified installer behind under a name that looks finished. That includes the
    /// stream's final flush, which is where a disk-full <see cref="IOException"/> actually surfaces -
    /// <see cref="FileStream"/> buffers, so the tail of the transfer is written at dispose rather than
    /// by the last write. The dispose is therefore inside the same <c>try</c> as the transfer.</para>
    /// </summary>
    /// <param name="url">Direct download URL of the asset.</param>
    /// <param name="destPath">File to write, created or truncated. Deleted again if the transfer fails or the checksum does not match.</param>
    /// <param name="progress">Optional sink for fractional progress (0.0-1.0); reported only when the response carries a Content-Length.</param>
    /// <param name="expectedSha256">
    /// Hex SHA-256 the downloaded bytes must hash to, or <see langword="null"/> to skip verification.
    /// <para>This is the only integrity check on the path that launches an installer elevated: TLS
    /// authenticates github.com in transit, but nothing else establishes that the file on disk is the
    /// one the release published. The hash is computed <b>as the bytes stream past</b>, so a 69 MB
    /// installer costs no second pass. A mismatch throws <see cref="InvalidDataException"/>, which the
    /// catch below turns into the same delete-the-partial-file cleanup as a failed transfer, so nothing
    /// unverified is ever left under a name that looks finished.</para>
    /// <para>Null means "could not verify", not "verified": the caller supplies whatever the GitHub API
    /// reported, and that can legitimately be absent. Verification is therefore a check on the download,
    /// not a guarantee to the user - the MSI is unsigned, so the UAC prompt still shows an unknown
    /// publisher either way.</para>
    /// </param>
    /// <param name="cancellationToken">Cancels the transfer; the partial file is removed on the way out.</param>
    public static async Task DownloadFileAsync(string url, string destPath, IProgress<double>? progress,
        string? expectedSha256 = null, CancellationToken cancellationToken = default)
    {
        using var response = await _downloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        // Same incremental idiom as NicotinePluginInstaller.ComputeBundledFingerprint. Left null when
        // there is nothing to compare against, so the unverified path allocates nothing.
        using var hash = expectedSha256 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash?.AppendData(buffer, 0, read);
                received += read;
                if (total is > 0)
                    progress?.Report((double)received / total.Value);
            }

            // Inside the try on purpose: the catch below owns the cleanup, so a mismatch deletes the
            // file by the same path a truncated transfer does rather than needing its own copy.
            if (hash is not null)
            {
                string actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"the downloaded installer does not match the checksum GitHub published for it " +
                        $"(expected {expectedSha256!.ToLowerInvariant()}, got {actual.ToLowerInvariant()})");
                }
            }

            // Inside the try for the same reason, and it is not merely tidy: FileStream buffers, so
            // the tail of the transfer is written here rather than by the last WriteAsync. A disk-full
            // or IO fault at this flush throws IOException, and with this outside the try it escaped
            // past the cleanup - leaving a truncated installer in %TEMP% under the finished name, the
            // one outcome the catch below exists to prevent. DisposeAsync is idempotent, so the
            // catch's own dispose stays a harmless no-op on every path that reaches it.
            await dest.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // A cancelled or failed transfer must not leave a truncated installer behind under a name
            // that looks like a finished one. The caller never runs it - every failure path falls back
            // to the release page and returns before launching - but the file would sit in %TEMP% as
            // "qbPortWeaver_<version>_Setup.msi", where someone sent to the release page could find and
            // run it by hand. Same rule as AppFiles.WriteAtomicCore: a write that did not complete
            // leaves nothing behind.
            //
            // Disposed explicitly first: `await using` has not run at this point and FileShare.None
            // holds the handle, so the delete would fail with the file still open.
            await dest.DisposeAsync().ConfigureAwait(false);
            try { File.Delete(destPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort. The next attempt opens with FileMode.Create and truncates it anyway.
                LogManager.Instance.LogDebug($"UpdateChecker.DownloadFileAsync: could not remove the partial download '{destPath}': {ex.Message}");
            }
            throw;
        }
    }

    private static bool IsBot(string login, string type) =>
        type.Equals("Bot", StringComparison.OrdinalIgnoreCase) ||
        login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);
}
