namespace qbPortWeaver;

/// <summary>
/// Small modal asking for a start and end instant, used by the Log Viewer's "Custom range…" option.
/// Built in code (no designer) because it is trivial and matches ClientChooserForm's shape.
/// <para>Exists because the preset ranges all end at "now", which cannot isolate a window that has
/// already passed - reading back over last night's outage is the case this filter was added for.</para>
/// </summary>
internal sealed class TimeRangeForm : Form
{
    private readonly DateTimePicker _from;
    private readonly DateTimePicker _to;

    /// <summary>Start of the chosen window (inclusive).</summary>
    internal DateTime FromValue => _from.Value;

    /// <summary>End of the chosen window (inclusive to the second).</summary>
    internal DateTime ToValue => _to.Value;

    internal TimeRangeForm(DateTime from, DateTime to)
    {
        Text = $"{AppIdentity.AppName} | Custom Time Range";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Icon = Properties.Resources.qbPortWeaver;
        ShowIcon = true;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _from = CreatePicker(from);
        _to = CreatePicker(to);

        // Deliberately unpadded: the dialog's inner margin belongs on the root panel below, so the
        // field rows and the button row share one set of edges. Padding the fields alone leaves the
        // buttons hanging off the bottom-right corner with no margin of their own.
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0),
        };
        grid.Controls.Add(CreateLabel("From:"), 0, 0);
        grid.Controls.Add(_from, 1, 0);
        grid.Controls.Add(CreateLabel("To:"), 0, 1);
        grid.Controls.Add(_to, 1, 1);

        var ok = DialogLayout.DialogButton("OK", DialogResult.OK);
        var cancel = DialogLayout.DialogButton("Cancel", DialogResult.Cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(DialogLayout.EdgeMargin, DialogLayout.EdgeMargin, DialogLayout.EdgeMargin, DialogLayout.BottomMargin),
        };
        root.Controls.Add(grid, 0, 0);
        root.Controls.Add(DialogLayout.ButtonRow(ok, cancel), 0, 1);
        Controls.Add(root);
    }

    // Vertical margin on each field; doubled between the two rows, so this is half the row gap.
    private const int RowSpacing = 4;

    // Gap kept between the end of the caption text and the close button.
    private const int CaptionPadding = 24;

    /// <summary>
    /// Widens the dialog when its caption is longer than its contents, which are only two short
    /// fields. The title bar takes no part in layout, so an auto-sized form is free to end up
    /// narrower than its own title and render it truncated.
    /// <para>Measured rather than hard-coded, because the caption is drawn in the system caption
    /// font rather than the form font, so it does not follow the form's own font scaling. Applied
    /// here rather than in the constructor so it lands after <see cref="AutoScaleMode.Font"/>
    /// scaling has run, which would otherwise scale an already-correct pixel width a second
    /// time.</para>
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        int caption = TextRenderer.MeasureText(Text, SystemFonts.CaptionFont ?? Font).Width;
        int chrome = SystemInformation.SmallIconSize.Width              // window icon
                   + SystemInformation.CaptionButtonSize.Width          // close button
                   + (SystemInformation.FixedFrameBorderSize.Width * 2)
                   + CaptionPadding;

        MinimumSize = new Size(caption + chrome, 0);
        CenterToParent();   // re-centre: the width above is applied after the initial placement
    }

    // Right-anchored so the two colons line up down the column. Anchoring without Top or Bottom
    // centres the label against the taller picker beside it instead of pinning it to the cell top.
    private static Label CreateLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Right,
        Margin = new Padding(0, 0, DialogLayout.Gap, 0),
    };

    // Custom format with a visible seconds field: log entries are timestamped to the second, and a
    // window chosen only to the minute cannot isolate a burst inside one.
    private static DateTimePicker CreatePicker(DateTime value) => new()
    {
        Format = DateTimePickerFormat.Custom,
        CustomFormat = LoggingConstants.DateFormat,
        ShowUpDown = true,          // no drop-down calendar: the field is edited in place, keeping the dialog small
        Value = value,
        Width = 170,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, RowSpacing, 0, RowSpacing),
    };

    /// <summary>Ensures the returned window is ordered, so a user who fills the fields in the other
    /// order still gets the range they meant rather than one that matches nothing.</summary>
    internal (DateTime From, DateTime To) OrderedRange() =>
        FromValue <= ToValue ? (FromValue, ToValue) : (ToValue, FromValue);
}
