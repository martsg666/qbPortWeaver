using qbPortWeaver.Shared;

namespace qbPortWeaver;

/// <summary>Non-modal notification shown when a newer version of the application is available.</summary>
internal sealed partial class UpdateAvailableForm : Form
{
    private readonly string _version;
    private readonly string _url;

    internal UpdateAvailableForm(string version, string url)
    {
        _version = version;
        _url = url;
        InitializeComponent();
        lblMessage.Text = $"Version {version} of {AppIdentity.AppName} is available.\n\nClick Update to open the download page.";
        Text = $"{AppIdentity.AppName} | Update Available";
    }

    private void btnUpdate_Click(object? sender, EventArgs e)
    {
        LogManager.Instance.LogMessage($"Update dialog: opening release page for {_version}", LogLevel.Info);
        AppConstants.OpenUrl(_url);
        Close();
    }

    private void btnLater_Click(object? sender, EventArgs e)
    {
        LogManager.Instance.LogMessage($"Update dialog: deferred update to {_version}", LogLevel.Info);
        Close();
    }
}
