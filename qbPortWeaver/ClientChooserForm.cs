namespace qbPortWeaver;

/// <summary>
/// Small modal prompt shown when client detection is ambiguous - more than one client is running, or
/// several are installed and none is running - so qbPortWeaver cannot pick one on its own. Lists the
/// detected clients with their status and lets the user choose. Built in code (no designer) because it
/// is trivial and its content is dynamic.
/// </summary>
internal sealed class ClientChooserForm : Form
{
    private const int RowHeight = 28;

    private readonly RadioButton[] _options;
    private readonly IReadOnlyList<ClientDetector.DetectedClient> _clients;

    /// <summary>The client the user selected, or <see langword="null"/> if the dialog was cancelled.</summary>
    internal ClientDetector.DetectedClient? SelectedClient { get; private set; }

    internal ClientChooserForm(IReadOnlyList<ClientDetector.DetectedClient> clients, string? preferredClientName)
    {
        _clients = clients;

        Text = $"{AppIdentity.AppName} | Detect Client";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        // Match the designer forms' autoscale baseline (9pt Segoe UI at 96 DPI) so the runtime scales
        // these manually-placed controls on high-DPI displays instead of leaving them at design pixels.
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(330, 100 + clients.Count * RowHeight);

        Controls.Add(new Label
        {
            Text = "More than one client was found. Select the one to use:",
            Location = new Point(12, 12),
            Size = new Size(306, 32),
            AutoSize = false
        });

        _options = new RadioButton[clients.Count];
        for (int i = 0; i < clients.Count; i++)
        {
            var c = clients[i];
            string status = c.Kind == ClientDetector.DetectionKind.Running ? "running now" : "installed";
            var rb = new RadioButton
            {
                Text = $"{c.ClientName} ({status})",
                Location = new Point(20, 50 + i * RowHeight),
                Size = new Size(290, 24),
                Checked = c.ClientName == preferredClientName
            };
            _options[i] = rb;
            Controls.Add(rb);
        }
        // Default to the currently selected client if it is among the matches, otherwise the first.
        if (!Array.Exists(_options, o => o.Checked) && _options.Length > 0)
            _options[0].Checked = true;

        int btnY = 50 + clients.Count * RowHeight + 12;
        var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(ClientSize.Width - 178, btnY), Size = new Size(80, 26) };
        btnOk.Click += CaptureSelection;
        var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(ClientSize.Width - 92, btnY), Size = new Size(80, 26) };

        Controls.Add(btnOk);
        Controls.Add(btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void CaptureSelection(object? sender, EventArgs e)
    {
        for (int i = 0; i < _options.Length; i++)
        {
            if (_options[i].Checked)
            {
                SelectedClient = _clients[i];
                return;
            }
        }
    }
}
