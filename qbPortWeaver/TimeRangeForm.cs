namespace qbPortWeaver;

/// <summary>
/// Small modal asking for a start and end instant, used by the Log Viewer's "Custom range…" option.
/// Built in code (no designer) because it is trivial and matches ClientChooserForm's shape.
/// <para>Exists because the preset ranges all end at "now", which cannot isolate a window that has
/// already passed - reading back over last night's outage is the case this filter was added for.</para>
/// </summary>
internal sealed class TimeRangeForm : Form
{
    private readonly MaskedTextBox _from;
    private readonly MaskedTextBox _to;

    // Set only once both fields have parsed, which OnFormClosing requires before it will let the
    // dialog close with OK. Reading them after any other result is meaningless.
    private DateTime _fromValue;
    private DateTime _toValue;

    /// <summary>Start of the chosen window (inclusive). Valid only after the dialog returned OK.</summary>
    internal DateTime FromValue => _fromValue;

    /// <summary>End of the chosen window (inclusive to the second). Valid only after the dialog returned OK.</summary>
    internal DateTime ToValue => _toValue;

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

        _fromValue = from;
        _toValue = to;
        _from = CreateField(from);
        _to = CreateField(to);

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
        // Labels take exactly their text width; the fields absorb everything left over, so their
        // right edge lands on the same content edge as the right-aligned button row below. This is
        // what keeps them aligned when the dialog is widened to fit its caption.
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
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
        // Fill rather than auto-size: when the caption widens the dialog past its contents, an
        // auto-sized column would keep the content block at its natural width and leave the surplus
        // as dead space on the right, pulling the fields and buttons away from the true right edge.
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
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
    // centres the label against the taller field beside it instead of pinning it to the cell top.
    private static Label CreateLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Right,
        Margin = new Padding(0, 0, DialogLayout.Gap, 0),
    };

    // Digit-and-separator mask matching LoggingConstants.DateFormat position for position, so the
    // field can only ever hold something shaped like a log timestamp. It constrains shape only, not
    // range - month 19 still has to be caught by the parse in TryReadValues.
    private const string TimestampMask = "0000-00-00 00:00:00";

    /// <summary>
    /// A masked entry field pre-filled with the given instant. Seconds are part of the mask because
    /// log entries are timestamped to the second, and a window chosen only to the minute cannot
    /// isolate a burst inside one.
    /// <para>A MaskedTextBox rather than a DateTimePicker: a DateTimePicker cannot be given the app's
    /// input surface, because assigning BackColor drops it out of the native dark theme and forces a
    /// white face. Being a TextBox, this honours the color like every other field in the app - at the
    /// cost of the picker's spin arrows, which the mask partly makes up for by rejecting stray
    /// characters as they are typed.</para>
    /// </summary>
    private static MaskedTextBox CreateField(DateTime value) => new()
    {
        Mask = TimestampMask,
        Text = value.ToString(LoggingConstants.DateFormat, System.Globalization.CultureInfo.InvariantCulture),
        BackColor = SystemColors.Window,        // input surface, like every text box and combo in the app
        ForeColor = SystemColors.WindowText,
        Width = 170,                // floor only: the anchor below stretches the field past this
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(0, RowSpacing, 0, RowSpacing),
    };

    /// <summary>
    /// Blocks an OK that carries an unparseable timestamp, so the caller never receives a window the
    /// user did not mean. Cancel and the close button are left alone - a user backing out should not
    /// have to repair a field first.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK && !TryReadValues())
        {
            e.Cancel = true;
            ThemedMessageBox.Show(
                $"Enter both times as {LoggingConstants.DateFormat}, for example " +
                $"{DateTime.Now.ToString(LoggingConstants.DateFormat, System.Globalization.CultureInfo.InvariantCulture)}.",
                $"{AppIdentity.AppName} | Custom Time Range",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        base.OnFormClosing(e);
    }

    private bool TryReadValues() => TryReadField(_from, out _fromValue) && TryReadField(_to, out _toValue);

    // Exact-format, invariant parse: the fields mirror the log's own timestamp format, so a
    // culture-sensitive parse could read the same digits as a different date on some machines.
    private static bool TryReadField(MaskedTextBox field, out DateTime value) =>
        DateTime.TryParseExact(field.Text, LoggingConstants.DateFormat,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out value);

    /// <summary>Ensures the returned window is ordered, so a user who fills the fields in the other
    /// order still gets the range they meant rather than one that matches nothing.</summary>
    internal (DateTime From, DateTime To) OrderedRange() =>
        FromValue <= ToValue ? (FromValue, ToValue) : (ToValue, FromValue);
}
