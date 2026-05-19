using qbPortWeaver.Shared;

namespace qbPortWeaver;

/// <summary>Non-modal notification shown when a newer version of the application is available.</summary>
internal sealed class UpdateAvailableForm : Form
{
    private readonly Font _titleFont = new("Segoe UI", 13F, FontStyle.Bold);

    internal UpdateAvailableForm(string version, string url)
    {
        var lblTitle = new Label
        {
            Font = _titleFont,
            Text = "Update Available",
            Location = new Point(8, 14),
            Size = new Size(384, 26),
            TabIndex = 0,
        };

        var grpInfo = new GroupBox
        {
            Text = "New Version",
            Location = new Point(8, 48),
            Size = new Size(384, 92),
            TabIndex = 1,
            TabStop = false,
        };

        var lblMessage = new Label
        {
            Text = $"Version {version} of {AppIdentity.AppName} is available.\n\nClick Update to open the download page.",
            AutoSize = false,
            Location = new Point(12, 20),
            Size = new Size(360, 58),
            TabIndex = 0,
        };

        var btnUpdate = new Button
        {
            Text = "Update",
            Location = new Point(220, 152),
            Size = new Size(82, 28),
            TabIndex = 2,
        };
        btnUpdate.Click += (_, _) =>
        {
            LogManager.Instance.LogMessage($"Update dialog: opening release page for {version}", LogLevel.Info);
            AppConstants.OpenUrl(url);
            Close();
        };

        var btnLater = new Button
        {
            Text = "Later",
            Location = new Point(310, 152),
            Size = new Size(82, 28),
            TabIndex = 3,
        };
        btnLater.Click += (_, _) =>
        {
            LogManager.Instance.LogMessage($"Update dialog: deferred update to {version}", LogLevel.Info);
            Close();
        };

        AcceptButton = btnUpdate;
        CancelButton = btnLater;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = $"{AppIdentity.AppName} | Update Available";

        grpInfo.SuspendLayout();
        SuspendLayout();
        grpInfo.Controls.Add(lblMessage);
        Controls.Add(lblTitle);
        Controls.Add(grpInfo);
        Controls.Add(btnUpdate);
        Controls.Add(btnLater);
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(400, 192);
        grpInfo.ResumeLayout(false);
        grpInfo.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _titleFont.Dispose();
        base.Dispose(disposing);
    }
}
