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

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(DialogLayout.EdgeMargin, DialogLayout.EdgeMargin, DialogLayout.EdgeMargin, DialogLayout.BottomMargin),
        };
        grid.Controls.Add(new Label { Text = "From:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, 0);
        grid.Controls.Add(_from, 1, 0);
        grid.Controls.Add(new Label { Text = "To:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, 1);
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
        };
        root.Controls.Add(grid, 0, 0);
        root.Controls.Add(DialogLayout.ButtonRow(ok, cancel), 0, 1);
        Controls.Add(root);
    }

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
        Margin = new Padding(0, 3, 0, 3),
    };

    /// <summary>Ensures the returned window is ordered, so a user who fills the fields in the other
    /// order still gets the range they meant rather than one that matches nothing.</summary>
    internal (DateTime From, DateTime To) OrderedRange() =>
        FromValue <= ToValue ? (FromValue, ToValue) : (ToValue, FromValue);
}
