using System.Diagnostics;

namespace qbPortWeaver;

/// <summary>
/// Notification shown when a newer version is available. Offers an in-app "Download &amp; Install" that
/// pulls the release's MSI, launches it (the installer relaunches the updated app), and exits. Falls
/// back to opening the release page when the release has no MSI asset or a download/launch fails.
/// </summary>
internal sealed partial class UpdateAvailableForm : Form
{
    private readonly string _version;
    private readonly string _releaseUrl;
    private readonly string? _msiUrl;
    private CancellationTokenSource? _downloadCts;
    private bool _downloading;

    internal UpdateAvailableForm(string version, string releaseUrl, string? msiUrl)
    {
        _version = version;
        _releaseUrl = releaseUrl;
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
        if (AppConstants.IsDarkModeEnabled())
            lnkReleaseNotes.LinkColor = AppConstants.DarkModeLinkColor;
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
            await UpdateChecker.DownloadFileAsync(msiUrl, destPath, progress, _downloadCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (IsDisposed) return;
            SetDownloadingUi(false);
            lblStatus.Text = "Download cancelled.";
            LogManager.Instance.LogMessage("Update download cancelled", LogLevel.Info);
            return;
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            SetDownloadingUi(false);
            lblStatus.Text = "Download failed - opening the release page instead.";
            LogManager.Instance.LogMessage($"Update download failed: {ex.Message} - falling back to the release page", LogLevel.Warn);
            AppConstants.OpenUrl(_releaseUrl);
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
            AppConstants.OpenUrl(_releaseUrl);
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
            _downloadCts?.Cancel();
            return;
        }
        LogManager.Instance.LogMessage($"Update dialog: deferred update to {_version}", LogLevel.Info);
        Close();
    }

    private void lnkReleaseNotes_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e) =>
        AppConstants.OpenUrl(_releaseUrl);

    private void OpenReleasePageAndClose()
    {
        LogManager.Instance.LogMessage($"Update dialog: opening release page for {_version}", LogLevel.Info);
        AppConstants.OpenUrl(_releaseUrl);
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
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        base.OnFormClosed(e);
    }
}
