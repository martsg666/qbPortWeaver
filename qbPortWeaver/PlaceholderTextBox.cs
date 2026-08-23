using System.ComponentModel;

namespace qbPortWeaver;

// Custom TextBox that draws a vertically-centered placeholder. Shadowing PlaceholderText
// prevents the base class from sending EM_SETCUEBANNER, whose native rendering ignores the
// app's color mode (light themed background) and sits at the top-left regardless of the
// control height. Shared by the log viewer's and help viewer's search boxes.
internal sealed class PlaceholderTextBox : TextBox
{
    private const int WM_PAINT = 0x000F;

    private string _placeholderText = string.Empty;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public new string PlaceholderText
    {
        get => _placeholderText;
        set { _placeholderText = value; Invalidate(); }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_PAINT && TextLength == 0 && !Focused && _placeholderText.Length > 0)
        {
            using var g = Graphics.FromHwnd(Handle);
            var rect = ClientRectangle;
            rect.Inflate(-2, 0);
            TextRenderer.DrawText(g, _placeholderText, Font, rect, SystemColors.GrayText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }
}
