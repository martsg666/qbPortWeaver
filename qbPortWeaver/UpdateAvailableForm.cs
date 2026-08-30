using System.Diagnostics;

namespace qbPortWeaver;

/// <summary>
/// Notification shown when a newer version is available. Offers an in-app "Download &amp; Install" that
/// pulls the release's MSI, launches it (the installer relaunches the updated app), and exits. Falls
/// back to opening the release page when the release has no MSI asset or a download/launch fails.
/// </summary>
internal sealed partial class UpdateAvailableForm : Form
{
    // The whole release record rather than the two or three fields the form happens to use today:
    // the MSI checksum was the fourth value this had to carry, and threading each new one through
    // MainForm's pending-update tuple and this constructor was already the awkward part.
    private readonly LatestReleaseInfo _release;
    private readonly string _version;
    private readonly string _releaseUrl;
    private readonly string? _msiUrl;
    private CancellationTokenSource? _downloadCts;
    private bool _downloading;
    private bool _userCancelled; // distinguishes a user Cancel from the safety-net timeout (both surface as OCE)

    public UpdateAvailableForm() : this(new LatestReleaseInfo(string.Empty, string.Empty, IsNewer: false)) { } // designer support only

    internal UpdateAvailableForm(LatestReleaseInfo release)
    {
        _release = release;
        string version = release.Version;
        string? msiUrl = release.MsiUrl;
        _version = version;
        _releaseUrl = release.ReleaseUrl;
        _msiUrl = msiUrl;
        InitializeComponent();
        Text = $"{AppIdentity.AppName} | Update Available";
        lblMessage.Text = $"Version {version} of {AppIdentity.AppName} is available.\n\n" +
            (msiUrl is not null
                ? $"Click Download & Install to download and install the update automatically. {AppIdentity.AppName} will restart when it finishes."
                : "Click Open Download Page to download the installer from GitHub.");
        btnUpdate.Text = msiUrl is not null ? "Download && Install" : "Open Download Page";
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (ThemeColors.IsDarkModeEnabled())
            lnkReleaseNotes.LinkColor = ThemeColors.LinkDark;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // The intrusive startup update check shows this non-modally while the tray-only app has no
        // foreground window, so it can open behind the window that launched us. Force it to the front.
        UiHelpers.BringFormToFront(this);
    }

    // Update button: in-app download+install when the release has an MSI asset, otherwise the release page.
    private async void btnUpdate_Click(object? sender, EventArgs e) // async void is correct here (WinForms event handler)
    {
        if (_msiUrl is null)
        {
            OpenReleasePageAndClose();
            return;
        }
        await DownloadAndInstallAsync(_msiUrl);
    }

    private async Task DownloadAndInstallAsync(string msiUrl)
    {
        string destPath = Path.Combine(Path.GetTempPath(), $"{AppIdentity.AppName}_{_version}_Setup.msi");
        _downloading = true;
        _userCancelled = false;
        SetDownloadingUi(true);
        _downloadCts?.Dispose(); // dispose any prior (cancelled) source before starting a fresh download
        _downloadCts = new CancellationTokenSource();
        _downloadCts.CancelAfter(TimeSpan.FromMinutes(20)); // safety net for a stalled connection; the user can also Cancel

        var progress = new Progress<double>(fraction =>
        {
            if (IsDisposed) return;
            int pct = (int)Math.Clamp(fraction * 100, 0, 100);
            prgDownload.Value = pct;
            lblStatus.Text = $"Downloading… {pct}%";
        });

        try
        {
            LogManager.Instance.LogMessage($"Downloading update {_version}", LogLevel.Info);
            await UpdateChecker.DownloadFileAsync(msiUrl, destPath, progress, _release.MsiSha256, _downloadCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (IsDisposed) return;
            SetDownloadingUi(false);
            if (_userCancelled)
            {
                lblStatus.Text = "Download cancelled.";
                LogManager.Instance.LogMessage("Update download cancelled", LogLevel.Info);
                return;
            }
            // Not user-initiated: the safety-net timeout fired on a stalled connection. Treat it like a
            // transfer failure and fall back to the release page so the user still has a way forward.
            lblStatus.Text = "Download timed out - opening the release page instead.";
            LogManager.Instance.LogMessage("Update download timed out - falling back to the release page", LogLevel.Warn);
            UiHelpers.OpenUrl(_releaseUrl);
            return;
        }
        // Separated from the generic failure below, and logged at Error rather than Warn: the transfer
        // succeeded and the bytes are wrong, which is a different and stronger signal than a stalled
        // connection - a corrupted mirror, a truncated asset, or something interfering with the
        // download. The partial file has already been deleted by DownloadFileAsync, so the release
        // page is the only way forward and the user is told why rather than just "download failed".
        catch (InvalidDataException ex)
        {
            if (IsDisposed) return;
            SetDownloadingUi(false);
            lblStatus.Text = "The download failed its integrity check - opening the release page instead.";
            LogManager.Instance.LogMessage(
                $"Update download rejected: {ex.Message}. The file was deleted; falling back to the release page",
                LogLevel.Error);
            UiHelpers.OpenUrl(_releaseUrl);
            return;
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            SetDownloadingUi(false);
            lblStatus.Text = "Download failed - opening the release page instead.";
            LogManager.Instance.LogMessage($"Update download failed: {ex.Message} - falling back to the release page", LogLevel.Warn);
            UiHelpers.OpenUrl(_releaseUrl);
            return;
        }
        finally
        {
            _downloading = false;
        }

        if (IsDisposed) return;
        LaunchInstallerAndExit(destPath);
    }

    // Launches the downloaded MSI interactively (msiexec via ShellExecute, with its UAC prompt) and
    // exits so the installer can replace the app's files; the installer's final step relaunches the
    // updated app. On launch failure, falls back to the release page.
    private void LaunchInstallerAndExit(string msiPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(msiPath) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            SetDownloadingUi(false);
            lblStatus.Text = "Could not launch the installer - opening the release page instead.";
            LogManager.Instance.LogMessage($"Failed to launch installer '{msiPath}': {ex.Message} - falling back to the release page", LogLevel.Error);
            UiHelpers.OpenUrl(_releaseUrl);
            return;
        }

        lblStatus.Text = $"Installer launched. {AppIdentity.AppName} will now close to update.";
        LogManager.Instance.LogMessage($"Update installer launched; {AppIdentity.AppName} exiting to allow the update", LogLevel.Info);
        Application.Exit();
    }

    // Later button doubles as Cancel while a download is in progress.
    private void btnLater_Click(object? sender, EventArgs e)
    {
        if (_downloading)
        {
            _userCancelled = true;
            _downloadCts?.Cancel();
            return;
        }
        LogManager.Instance.LogMessage($"Update dialog: deferred update to {_version}", LogLevel.Info);
        Close();
    }

    private void lnkReleaseNotes_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e) =>
        UiHelpers.OpenUrl(_releaseUrl);

    private void OpenReleasePageAndClose()
    {
        LogManager.Instance.LogMessage($"Update dialog: opening release page for {_version}", LogLevel.Info);
        UiHelpers.OpenUrl(_releaseUrl);
        Close();
    }

    // Shows the progress bar/status and turns Later into Cancel while a download runs; restores on stop.
    private void SetDownloadingUi(bool downloading)
    {
        prgDownload.Visible = downloading;
        lblStatus.Visible = true;
        btnUpdate.Enabled = !downloading;
        btnLater.Text = downloading ? "Cancel" : "Later";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _userCancelled = true; // closing the dialog mid-download is a cancel, not the timeout fallback
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        base.OnFormClosed(e);
    }
}
