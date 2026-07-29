namespace qbPortWeaver;

/// <summary>
/// Shared layout primitives for the app's code-built dialogs (ThemedMessageBox, ClientChooserForm,
/// DiagnosticsForm). These dialogs have dynamic content, so they are built in code rather than the
/// designer; routing their chrome through here keeps button sizing, spacing, and the right-aligned
/// button row identical across all three. Layout uses the automatic layout containers
/// (TableLayoutPanel / FlowLayoutPanel), which size and position at the current DPI, so the dialogs
/// scale correctly without hand-computed pixel coordinates.
/// </summary>
internal static class DialogLayout
{
    internal const int EdgeMargin = 16;    // dialog inner padding (left/top/right)
    internal const int BottomMargin = 12;  // dialog inner padding (bottom)
    internal const int Gap = 8;            // gap between adjacent buttons

    internal const int ButtonWidth = 82;
    internal const int ButtonHeight = 28;

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
