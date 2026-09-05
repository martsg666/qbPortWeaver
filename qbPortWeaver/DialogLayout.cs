namespace qbPortWeaver;

/// <summary>
/// Shared layout primitives for the app's code-built dialogs (ThemedMessageBox, ClientChooserForm,
/// TimeRangeForm, DiagnosticsForm). These dialogs have dynamic content, so they are built in code
/// rather than the designer; routing their chrome through here keeps button sizing, spacing, and the
/// right-aligned button row identical across all four. Layout uses the automatic layout containers
/// (TableLayoutPanel / FlowLayoutPanel), which size and position at the current DPI, so the dialogs
/// scale correctly without hand-computed pixel coordinates.
/// <para><b>Not every member applies to every caller.</b> The button metrics are used by all four, but
/// <see cref="EdgeMargin"/> and <see cref="BottomMargin"/> are for the dialogs that auto-size to their
/// content (ThemedMessageBox, ClientChooserForm, TimeRangeForm), which need breathing room around a
/// handful of controls. DiagnosticsForm has a fixed 560x690 ClientSize and uses a uniform 8px inset
/// instead, matching the designer-built windows (StatusForm, LogViewerForm, SettingsForm) - the same
/// margin in a dense report view would only waste space. Do not "fix" that to 16 for consistency: the
/// split tracks whether the form sizes itself, not how it was built.</para>
/// </summary>
internal static class DialogLayout
{
    internal const int EdgeMargin = 16;    // auto-sized dialog inner padding (left/top/right); see the note above
    internal const int BottomMargin = 12;  // auto-sized dialog inner padding (bottom)
    internal const int Gap = 8;            // gap between adjacent buttons

    internal const int ButtonWidth = 82;
    internal const int ButtonHeight = 28;

    /// <summary>
    /// Applies the window chrome every code-built dialog shares: a fixed frame, no minimise or
    /// maximise box, no taskbar entry, the app icon, and the designer's font-scaling baseline.
    /// </summary>
    /// <remarks>
    /// <see cref="Form.StartPosition"/> and <see cref="Form.AutoSize"/> are deliberately left to the
    /// caller, because they are the two that genuinely differ: the auto-sized dialogs centre on their
    /// owner, while DiagnosticsForm centres on the screen and sizes itself from its content.
    /// <para>The window icon is what Alt+Tab draws. Leaving it unset gave every dialog a blank entry
    /// there, so it is set here as on every other form; the title bar stays iconless because a
    /// FixedDialog frame does not draw one, which is what the "no title-bar icon" convention actually
    /// relies on - <c>ShowIcon = false</c> was suppressing the Alt+Tab icon as a side effect. That
    /// reasoning applied to all four dialogs but lived in only one of them, which is why this moved.</para>
    /// <para>Call it where the property block used to sit, before controls are added: the scaling
    /// pair has to be set before the layout containers measure, exactly as the designer emits it.</para>
    /// </remarks>
    internal static void ApplyDialogChrome(Form form)
    {
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MinimizeBox = false;
        form.MaximizeBox = false;
        form.ShowInTaskbar = false;
        form.Icon = Properties.Resources.qbPortWeaver;
        form.ShowIcon = true;
        // The layout containers size the form; AutoScaleMode.Font (designer baseline) scales fonts.
        form.AutoScaleDimensions = new SizeF(7F, 15F);
        form.AutoScaleMode = AutoScaleMode.Font;
    }

    /// <summary>
    /// The outer content panel for an auto-sized dialog: one column, a content row above a button
    /// row, and the standard inner padding. Callers add their content at (0,0) and a
    /// <see cref="ButtonRow"/> at (0,1).
    /// </summary>
    /// <remarks>Only the auto-sized dialogs use this (ThemedMessageBox, ClientChooserForm,
    /// TimeRangeForm) - DiagnosticsForm builds its own around a fixed <c>ClientSize</c>. A caller
    /// that needs the single column to fill rather than hug its content adds its own
    /// <see cref="ColumnStyle"/>; that is a real difference between these dialogs, not an oversight,
    /// so it stays at the call site.</remarks>
    internal static TableLayoutPanel ContentRoot()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(EdgeMargin, EdgeMargin, EdgeMargin, BottomMargin),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // content
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // button row
        return root;
    }

    /// <summary>A standard 82x28 dialog button carrying the given result, with no auto-margin.</summary>
    internal static Button DialogButton(string text, DialogResult result) => new()
    {
        Text = text,
        DialogResult = result,
        Size = new Size(ButtonWidth, ButtonHeight),
        Margin = new Padding(0),
    };

    /// <summary>
    /// A right-aligned button row for the bottom of a dialog. Pass the buttons in left-to-right display
    /// order (affirmative first, dismiss last - the app convention); they are laid out right-aligned with
    /// a uniform gap. The row carries the standard top margin that separates it from the content above.
    /// </summary>
    internal static FlowLayoutPanel ButtonRow(params Button[] buttonsLeftToRight)
    {
        var panel = new FlowLayoutPanel
        {
            // RightToLeft so the group hugs the right edge; first-added control lands rightmost.
            FlowDirection = FlowDirection.RightToLeft,
            Anchor = AnchorStyles.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, EdgeMargin, 0, 0),
            Padding = new Padding(0),
        };
        for (int i = buttonsLeftToRight.Length - 1; i >= 0; i--)
        {
            // Left margin creates the inter-button gap; the leftmost button (i == 0) sits flush.
            buttonsLeftToRight[i].Margin = new Padding(i == 0 ? 0 : Gap, 0, 0, 0);
            panel.Controls.Add(buttonsLeftToRight[i]);
        }
        return panel;
    }
}
