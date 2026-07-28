namespace qbPortWeaver;

/// <summary>
/// Small modal prompt shown when client detection is ambiguous - more than one client is running, or
/// several are installed and none is running - so qbPortWeaver cannot pick one on its own. Lists the
/// detected clients with their status and lets the user choose. Built in code (no designer) because it
/// is trivial and its content is dynamic.
/// </summary>
internal sealed class ClientChooserForm : Form
{
    // Wrap width for the prompt label and the width the radio list fills toward.
    private const int ContentWidth = 300;

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
        ShowIcon = false; // match the other modal dialogs (no title-bar icon)
        // The layout containers size the form; AutoScaleMode.Font (designer baseline) scales fonts.
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(DialogLayout.EdgeMargin, DialogLayout.EdgeMargin, DialogLayout.EdgeMargin, DialogLayout.BottomMargin),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // prompt + options
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons

        var content = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };
        content.Controls.Add(new Label
        {
            Text = "More than one client was found. Select the one to use:",
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            Margin = new Padding(0, 0, 0, 8),
        });

        _options = new RadioButton[clients.Count];
        for (int i = 0; i < clients.Count; i++)
        {
            var c = clients[i];
            string status = c.Kind == ClientDetector.DetectionKind.Running ? "running now" : "installed";
            var rb = new RadioButton
            {
                Text = $"{c.ClientName} ({status})",
                AutoSize = true,
                Checked = c.ClientName == preferredClientName,
                Margin = new Padding(16, 2, 0, 2), // indent under the prompt
            };
            _options[i] = rb;
            content.Controls.Add(rb);
        }
        // Default to the currently selected client if it is among the matches, otherwise the first.
        if (!Array.Exists(_options, o => o.Checked) && _options.Length > 0)
            _options[0].Checked = true;
        root.Controls.Add(content, 0, 0);

        var btnOk = DialogLayout.DialogButton("OK", DialogResult.OK);
        btnOk.Click += CaptureSelection;
        var btnCancel = DialogLayout.DialogButton("Cancel", DialogResult.Cancel);
        root.Controls.Add(DialogLayout.ButtonRow(btnOk, btnCancel), 0, 1);
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        Controls.Add(root);
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
