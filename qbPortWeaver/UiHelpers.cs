using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace qbPortWeaver;

/// <summary>Shared WinForms helpers: owner-drawn glyphs, the search-toolbar layout used by the log and help
/// viewers, foreground activation for a tray app with no owner window, and clipboard access that
/// tolerates another process holding the clipboard open.</summary>
public static class UiHelpers
{
    /// <summary>
    /// Owner-draws a crisp up/down chevron centered in a nav button, in the button's ForeColor.
    /// Drawn instead of a font glyph so it is always centered and its size/weight are exact.
    /// Shared by the log viewer's and help viewer's search-nav Paint handlers.
    /// </summary>
    public static void DrawNavChevron(Button btn, Graphics g, bool up)
    {
        float scale = btn.DeviceDpi / 96f;
        float halfW = 5f * scale;     // chevron half-width
        float halfH = 3.25f * scale;  // chevron half-height
        float cx = btn.ClientSize.Width / 2f;
        float cy = btn.ClientSize.Height / 2f;
        float armY  = up ? cy + halfH : cy - halfH; // the two ends
        float apexY = up ? cy - halfH : cy + halfH; // the point

        PointF[] chevron = [new(cx - halfW, armY), new(cx, apexY), new(cx + halfW, armY)];

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(btn.ForeColor, 1.8f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawLines(pen, chevron);
    }

    /// <summary>
    /// Owner-draws the clear (X) glyph centered in the search box's clear button, in the button's
    /// ForeColor. Drawn rather than typed as the letter "X" for the same reason the nav chevrons
    /// are: exact size, weight and centering, and no dependency on a glyph the UI font may not
    /// carry. Shared by the log viewer's and help viewer's clear-button Paint handlers.
    /// </summary>
    public static void DrawClearGlyph(Button btn, Graphics g)
    {
        float scale = btn.DeviceDpi / 96f;
        float half = 3.25f * scale; // arm half-length, matching the chevrons' visual weight
        float cx = btn.ClientSize.Width / 2f;
        float cy = btn.ClientSize.Height / 2f;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(btn.ForeColor, 1.8f * scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawLine(pen, cx - half, cy - half, cx + half, cy + half);
        g.DrawLine(pen, cx - half, cy + half, cx + half, cy - half);
    }

    /// <summary>
    /// Lays out the right-aligned search group shared by the log viewer and help viewer toolbars:
    /// the search box, match counter, and prev/next nav buttons pinned to the toolbar's right edge,
    /// with the clear (×) button floating inside the search box. Positions are computed from the
    /// toolbar's actual client width (not designer coordinates) so the group lands correctly whatever
    /// the form padding or DPI. <paramref name="rightMargin"/> is the logical-pixel gap between the
    /// last button and the toolbar's right edge - 0 when the form's own padding already supplies the
    /// visual margin (help viewer), or the desired inset when it does not (log viewer). Vertical
    /// centering uses the search box's font-driven height, known only after layout, so call from OnLoad.
    /// </summary>
    public static void LayoutSearchToolbar(Control toolbar, TextBox search, Label matchCount, Button prev, Button next, Button clear, int rightMargin)
    {
        int gap = toolbar.LogicalToDeviceUnits(4);
        int margin = toolbar.LogicalToDeviceUnits(rightMargin);
        int top = (toolbar.Height - search.Height) / 2;

        // Right-aligned group, positioned from the toolbar's right edge inward.
        next.Left = toolbar.ClientSize.Width - margin - next.Width;
        prev.Left = next.Left - prev.Width;
        matchCount.Left = prev.Left - gap - matchCount.Width;
        search.Left = matchCount.Left - gap - search.Width;

        // Vertically center the group on the search box; the nav buttons match its height.
        search.Top = top;
        prev.Height = next.Height = search.Height;
        prev.Top = next.Top = top;
        matchCount.Top = top + (search.Height - matchCount.Height) / 2;

        // Clear button floats inside the right interior of the search box. The 4px inset shrinks it to
        // fit within the box's 2px top/bottom border; the 2px margin keeps it clear of the right edge.
        int size = search.Height - toolbar.LogicalToDeviceUnits(4);
        int clearMargin = toolbar.LogicalToDeviceUnits(2);
        clear.Size = new Size(size, size);
        clear.Location = new Point(search.Right - size - clearMargin, top + clearMargin);
        clear.BringToFront(); // must sit above the native TextBox HWND or it is hidden behind it
    }

    /// <summary>Opens a URL in the default browser using ShellExecute.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogMessage($"Failed to open URL '{url}': {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// Raises a form to the foreground. The tray-only app has no foreground window, so a form shown
    /// non-modally at startup (What's New, the update prompt) can open behind the window that launched
    /// us; this forces it up. The brief TopMost toggle raises the z-order past the foreground lock
    /// without leaving the window permanently always-on-top.
    /// </summary>
    public static void BringFormToFront(Form form)
    {
        form.BringToFront();
        form.TopMost = true;
        form.TopMost = false;
        form.Activate();
    }

    /// <summary>Copies text to the clipboard, swallowing the transient <see cref="ExternalException"/> thrown
    /// when another process holds the clipboard open (clipboard managers, RDP). Empty text is replaced with a
    /// single space because <see cref="Clipboard.SetText(string)"/> rejects an empty string.</summary>
    public static void SetClipboardTextSafely(string text)
    {
        try
        {
            Clipboard.SetText(string.IsNullOrEmpty(text) ? " " : text);
        }
        catch (ExternalException ex)
        {
            LogManager.Instance.LogDebug($"UiHelpers.SetClipboardTextSafely: Clipboard unavailable: {ex.Message}");
        }
    }

    /// <summary>Returns clipboard text, or <see langword="null"/> when the clipboard holds no text or is transiently locked by another process.</summary>
    public static string? TryGetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (ExternalException ex)
        {
            LogManager.Instance.LogDebug($"UiHelpers.TryGetClipboardText: Clipboard unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Returns <see langword="true"/> if the clipboard contains text; <see langword="false"/> if empty or transiently locked by another process.</summary>
    public static bool ClipboardHasText()
    {
        try
        {
            return Clipboard.ContainsText();
        }
        catch (ExternalException ex)
        {
            LogManager.Instance.LogDebug($"UiHelpers.ClipboardHasText: Clipboard unavailable: {ex.Message}");
            return false;
        }
    }
}
