using System.Text;

namespace qbPortWeaver;

/// <summary>
/// Read-only results dialog for a <see cref="DiagnosticsService"/> run: a color-coded checklist with a
/// per-check detail line and fix hint, a point-in-time timestamp, a Re-run button that refreshes the
/// report in place, and a Copy Report button that yields a plain-text version for pasting into a bug
/// report. Built in code (no designer) because its content is entirely dynamic.
/// </summary>
internal sealed class DiagnosticsForm : Form
{
    // Paragraph indent (pixels) for the detail/hint lines under each check - keeps wrapped lines aligned.
    private const int DetailIndentPixels = 22;

    // Resolved once when the dialog is created (a re-run keeps the same process, so these do not change).
    private readonly string _appVersion = DiagnosticsService.AppVersion;
    private readonly string? _helperVersion = DiagnosticsService.GetHelperServiceVersion();
    private IReadOnlyList<DiagnosticResult> _results;
    private DateTime _ranAt;
    private RichTextBox _report = null!;
    private Button _btnRerun = null!;

    /// <summary>Raised when the user clicks Re-run. MainForm handles it by re-running the diagnostics
    /// and pushing the new results back via <see cref="SetResults"/>, toggling the button via
    /// <see cref="SetRunning"/>.</summary>
    internal event EventHandler? RefreshRequested;

    internal DiagnosticsForm(IReadOnlyList<DiagnosticResult> results)
    {
        _results = results;
        _ranAt = DateTime.Now;

        Text = $"{AppIdentity.AppName} | Diagnostics ({_ranAt:HH:mm:ss})";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Icon     = Properties.Resources.qbPortWeaver;
        ShowIcon = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        // Match the designer forms' 96-DPI autoscale baseline so the manually-placed controls scale.
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        // Initial width; SizeToContent (after the report renders) sets the final height to fit.
        ClientSize = new Size(560, 690);

        BuildControls();
        RenderReport();
        SizeToContent();
    }

    /// <summary>Replaces the displayed report with a fresh run's results (updates the timestamp too).</summary>
    internal void SetResults(IReadOnlyList<DiagnosticResult> results)
    {
        if (IsDisposed) return;
        _results = results;
        _ranAt = DateTime.Now;
        Text = $"{AppIdentity.AppName} | Diagnostics ({_ranAt:HH:mm:ss})";
        RenderReport();
    }

    /// <summary>Toggles the Re-run button between its idle and in-progress states while a refresh runs.
    /// The previous report stays visible until the new results arrive.</summary>
    internal void SetRunning(bool running)
    {
        if (IsDisposed) return;
        _btnRerun.Enabled = !running;
        _btnRerun.Text = running ? "Running…" : "Re-run";
    }

    private void BuildControls()
    {
        // Control is the shared dialog surface, mode-aware under Application.SetColorMode, so the report
        // matches the rest of the app; only the status glyphs use accent colors. The RichTextBox needs it
        // set explicitly - it defaults to SystemColors.Window and does not inherit the form's BackColor.
        BackColor = SystemColors.Control;

        // A TableLayoutPanel positions the content at the current DPI (no hand-computed coordinates):
        // the report fills the top row and the button bar auto-sizes below it. The form keeps a fixed
        // width and a content-computed, capped height (SizeToContent), so it is not a pure AutoSize
        // dialog like the smaller code-built dialogs, but it shares the same layout-container approach.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // report fills the remaining height
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // button bar

        _report = new RichTextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            TabStop = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        root.Controls.Add(_report, 0, 0);

        // Button bar: Copy Report + Re-run grouped on the left, Close on the right.
        var buttonBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 8, 0, 0),
        };
        buttonBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // left group + spacer
        buttonBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));     // Close

        var leftGroup = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0),
        };
        var btnCopy = new Button { Text = "Copy Report", Size = new Size(110, DialogLayout.ButtonHeight), Margin = new Padding(0, 0, DialogLayout.Gap, 0) };
        btnCopy.Click += (_, _) => UiHelpers.SetClipboardTextSafely(BuildPlainReport());
        leftGroup.Controls.Add(btnCopy);

        _btnRerun = new Button { Text = "Re-run", Size = new Size(90, DialogLayout.ButtonHeight), Margin = new Padding(0) };
        _btnRerun.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        leftGroup.Controls.Add(_btnRerun);
        buttonBar.Controls.Add(leftGroup, 0, 0);

        var btnClose = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            Size = new Size(DialogLayout.ButtonWidth, DialogLayout.ButtonHeight),
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
        };
        btnClose.Click += (_, _) => Close(); // NOSONAR S2325 - Close() is an instance method, handler cannot be static
        buttonBar.Controls.Add(btnClose, 1, 0);

        root.Controls.Add(buttonBar, 0, 1);
        AcceptButton = btnClose;
        CancelButton = btnClose;

        Controls.Add(root);
    }

    // Renders the checklist with color runs: a summary line, a timestamp, then per-check a bold colored
    // glyph + check name, an indented detail line, and an indented gray hint line. SelectionFont
    // assignments apply RTF formatting immediately, so the transient fonts are safe to dispose after
    // this method returns (same pattern as WhatsNewForm).
    private void RenderReport()
    {
        Color textColor = SystemColors.WindowText;
        Color metaColor = SystemColors.GrayText;

        using var summaryFont = new Font(_report.Font.FontFamily, _report.Font.Size + 1.5f, FontStyle.Bold);
        using var boldFont = new Font(_report.Font, FontStyle.Bold);

        int pass = _results.Count(r => r.Status == DiagnosticStatus.Pass);
        int warn = _results.Count(r => r.Status == DiagnosticStatus.Warn);
        int fail = _results.Count(r => r.Status == DiagnosticStatus.Fail);

        _report.Clear();
        _report.ForeColor = textColor;

        // Summary line with colored counts; a zero count stays neutral so "0 failed" is not an alarming red.
        _report.SelectionIndent = 0;
        _report.SelectionFont = summaryFont;
        AppendColored($"{pass} passed", PassColor);
        AppendColored(", ", textColor);
        AppendColored(TextFormat.Pluralize(warn, "warning"), warn > 0 ? WarnColor : metaColor);
        AppendColored(", ", textColor);
        AppendColored($"{fail} failed\n", fail > 0 ? FailColor : metaColor);

        // Version subline: app and installed helper-service versions (surfaces a post-upgrade mismatch).
        _report.SelectionFont = _report.Font;
        _report.SelectionColor = metaColor;
        // Blank line before the version subline (space above it, not below), one blank line after.
        _report.AppendText($"\n{AppIdentity.AppName} {_appVersion}   •   Helper service {_helperVersion ?? "not installed"}\n\n");

        foreach (var r in _results)
        {
            var (glyph, color) = GlyphFor(r.Status);
            _report.SelectionIndent = 0;
            _report.SelectionFont = boldFont;
            _report.SelectionColor = color;
            _report.AppendText($"{glyph}  ");
            _report.SelectionColor = textColor;
            _report.AppendText($"{r.Check}\n");

            // Indent the detail/hint paragraphs so a wrapped second line stays aligned under the
            // first instead of falling back to the left margin (leading spaces only indent line one).
            // Scaled with DPI since RichTextBox.SelectionIndent is not part of WinForms' auto-scaling pipeline.
            _report.SelectionIndent = LogicalToDeviceUnits(DetailIndentPixels);
            _report.SelectionFont = _report.Font;
            _report.SelectionColor = textColor;
            _report.AppendText($"{r.Detail}\n");
            if (!string.IsNullOrEmpty(r.Hint))
            {
                _report.SelectionColor = metaColor;
                _report.AppendText($"{r.Hint}\n");
            }
            _report.SelectionIndent = 0;
            _report.AppendText("\n");
        }

        _report.Select(0, 0);
        _report.ScrollToCaret();
    }

    // Sizes this fixed dialog to fit the rendered report plus the button row, so a short report
    // (e.g. Transmission's 11 checks) leaves no dead space and Nicotine+'s fuller 13-check report
    // still fits without scrolling. Capped so a report with many fix hints scrolls rather than
    // growing off-screen. Runs once at construction; a Re-run keeps the size and scrolls if taller.
    private void SizeToContent()
    {
        int lineHeight = _report.Font.Height;
        int lines = 4; // summary line, blank, version subline, blank
        foreach (var r in _results)
            lines += string.IsNullOrEmpty(r.Hint) ? 3 : 4; // name + detail (+ hint) + trailing blank

        const int reportTop = 8;    // _report.Location.Y
        const int buttonArea = 44;  // report bottom to form bottom: 8px gap + 28px button + 8px margin
        int reportHeight = lines * lineHeight + lineHeight; // one extra line of breathing room
        int height = Math.Min(reportTop + reportHeight + buttonArea, 760);
        ClientSize = new Size(ClientSize.Width, height);
    }

    private static (string Glyph, Color Color) GlyphFor(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Pass => ("✓", PassColor),
        DiagnosticStatus.Warn => ("⚠", WarnColor),
        DiagnosticStatus.Fail => ("✗", FailColor),
        _ => ("–", SystemColors.GrayText),
    };

    private static Color PassColor => ThemeColors.StatusOk;
    private static Color WarnColor => ThemeColors.StatusWarning;
    private static Color FailColor => ThemeColors.StatusError;

    // Appends a colored run at the current caret, keeping the active SelectionFont.
    private void AppendColored(string text, Color color)
    {
        _report.SelectionColor = color;
        _report.AppendText(text);
    }

    // Plain-text version for the Copy Report button - safe to paste into a GitHub issue.
    private string BuildPlainReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{AppIdentity.AppName} {_appVersion} diagnostics - {_ranAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Helper service: {_helperVersion ?? "not installed"}");
        sb.AppendLine();
        foreach (var r in _results)
        {
            string tag = r.Status switch
            {
                DiagnosticStatus.Pass => "PASS",
                DiagnosticStatus.Warn => "WARN",
                DiagnosticStatus.Fail => "FAIL",
                _ => "SKIP",
            };
            sb.AppendLine($"[{tag}] {r.Check}: {r.Detail}");
            if (!string.IsNullOrEmpty(r.Hint))
                sb.AppendLine($"       {r.Hint}");
        }

        // Settings (sensitive values masked) so a pasted report is self-contained for a bug report.
        var snapshot = DiagnosticsService.GetSettingsSnapshot();
        if (snapshot.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Settings:");
            foreach (var (section, values) in snapshot)
            {
                sb.AppendLine($"  [{section}]");
                foreach (var (key, value) in values)
                    sb.AppendLine($"    {key} = {value}");
            }
        }
        return sb.ToString();
    }
}
