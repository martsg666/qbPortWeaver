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
    private readonly bool _isDarkMode;
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
        _isDarkMode = AppConstants.IsDarkModeEnabled();

        Text = $"{AppIdentity.AppName} | Diagnostics ({_ranAt:HH:mm:ss})";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        // Match the designer forms' 96-DPI autoscale baseline so the manually-placed controls scale.
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        // Sized to show a full 10-check report without scrolling in the common case; still resizable
        // and scrollable for a report with many fix hints.
        ClientSize = new Size(560, 640);
        MinimumSize = new Size(460, 400);

        BuildControls();
        RenderReport();
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
        Color bg = _isDarkMode ? AppConstants.DarkModeBackground : SystemColors.Window;
        BackColor = bg;

        _report = new RichTextBox
        {
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = bg,
            TabStop = false,
            Location = new Point(12, 12),
            Size = new Size(ClientSize.Width - 24, ClientSize.Height - 60),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_report);

        var btnCopy = new Button
        {
            Text = "Copy Report",
            Size = new Size(110, 28),
            Location = new Point(12, ClientSize.Height - 40),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        btnCopy.Click += (_, _) => AppConstants.TrySetClipboardText(BuildPlainReport());
        Controls.Add(btnCopy);

        _btnRerun = new Button
        {
            Text = "Re-run",
            Size = new Size(90, 28),
            Location = new Point(130, ClientSize.Height - 40),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
        };
        _btnRerun.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(_btnRerun);

        var btnClose = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            Size = new Size(82, 28),
            Location = new Point(ClientSize.Width - 94, ClientSize.Height - 40),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        btnClose.Click += (_, _) => Close(); // NOSONAR S2325 - Close() is an instance method, handler cannot be static
        Controls.Add(btnClose);

        AcceptButton = btnClose;
        CancelButton = btnClose;
    }

    // Renders the checklist with color runs: a summary line, a timestamp, then per-check a bold colored
    // glyph + check name, an indented detail line, and an indented gray hint line. SelectionFont
    // assignments apply RTF formatting immediately, so the transient fonts are safe to dispose after
    // this method returns (same pattern as WhatsNewForm).
    private void RenderReport()
    {
        Color textColor = _isDarkMode ? AppConstants.DarkModeText : SystemColors.WindowText;
        Color metaColor = _isDarkMode ? AppConstants.DarkModeSecondaryText : SystemColors.GrayText;

        using var summaryFont = new Font(_report.Font.FontFamily, _report.Font.Size + 1.5f, FontStyle.Bold);
        using var boldFont = new Font(_report.Font, FontStyle.Bold);

        int pass = _results.Count(r => r.Status == DiagnosticStatus.Pass);
        int warn = _results.Count(r => r.Status == DiagnosticStatus.Warn);
        int fail = _results.Count(r => r.Status == DiagnosticStatus.Fail);

        _report.Clear();
        _report.ForeColor = textColor;

        _report.SelectionFont = summaryFont;
        _report.SelectionColor = textColor;
        _report.AppendText($"{pass} passed, {warn} warning(s), {fail} failed\n\n");

        foreach (var r in _results)
        {
            var (glyph, color) = GlyphFor(r.Status);
            _report.SelectionFont = boldFont;
            _report.SelectionColor = color;
            _report.AppendText($"{glyph}  ");
            _report.SelectionColor = textColor;
            _report.AppendText($"{r.Check}\n");

            _report.SelectionFont = _report.Font;
            _report.SelectionColor = textColor;
            _report.AppendText($"      {r.Detail}\n");
            if (!string.IsNullOrEmpty(r.Hint))
            {
                _report.SelectionColor = metaColor;
                _report.AppendText($"      {r.Hint}\n");
            }
            _report.AppendText("\n");
        }

        _report.Select(0, 0);
        _report.ScrollToCaret();
    }

    private (string Glyph, Color Color) GlyphFor(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Pass => ("✓", _isDarkMode ? AppConstants.StatusOk : AppConstants.StatusOkLight),
        DiagnosticStatus.Warn => ("⚠", _isDarkMode ? AppConstants.StatusWarning : AppConstants.StatusWarningLight),
        DiagnosticStatus.Fail => ("✗", AppConstants.StatusError),
        _ => ("–", _isDarkMode ? AppConstants.DarkModeSecondaryText : SystemColors.GrayText),
    };

    // Plain-text version for the Copy Report button - safe to paste into a GitHub issue.
    private string BuildPlainReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{AppIdentity.AppName} {AppConstants.AppVersion} diagnostics - {_ranAt:yyyy-MM-dd HH:mm:ss}");
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
        return sb.ToString();
    }
}
