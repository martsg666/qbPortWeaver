namespace qbPortWeaver;

/// <summary>Displays the shipped user guide (README.md, installed next to the executable) rendered
/// with lightweight formatting, a table-of-contents tree built from the guide's headings, and
/// text search (Ctrl+F). Opened via the tray menu Help item; only one instance is allowed
/// at a time (enforced by MainForm.ShowOrActivate).</summary>
public partial class HelpForm : Form
{
    private const string GuideFileName = "README.md";

    // Clickable link runs in rtbHelp, tracked by character range. The content is rendered once
    // and the box is read-only, so the ranges stay valid for the form's lifetime.
    private readonly List<(int Start, int Length, string Url)> _links = new();

    // Headings in document order, recorded while rendering; BuildToc turns them into the
    // contents tree. CharIndex is the heading's start position in the rendered document.
    private readonly List<(int CharIndex, int Level, string Text)> _headings = new();

    // Search state: match start positions in document order, index of the current match, and
    // the rendered document's plain text cached once after rendering (RichTextBox.Text walks
    // the native control on every call, so per-keystroke scans use the cache).
    private readonly List<int> _searchMatches = new();
    private int _searchIndex = -1;
    private string _documentText = string.Empty;

    // Heading/inline fonts, created once in OnLoad for the active base font and disposed in the
    // Designer's Dispose.
    private Font? _h1Font;
    private Font? _h2Font;
    private Font? _h3Font;
    private Font? _h4Font;
    private Font? _boldFont;
    private Font? _italicFont;
    private Font? _monoFont;
    private Color _linkColor;

    public HelpForm()
    {
        InitializeComponent();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Text = $"{AppIdentity.AppName} | Help";
        KeyPreview = true; // form sees keys before the focused control - see OnKeyDown (Escape to close, Ctrl+F)
        ApplyTheme();

        // Lay out the right-aligned search group (shared with the log viewer). rightMargin 0: the
        // form's own right Padding already supplies the visual gap between the last button and the
        // window edge, so no extra inset is baked into the layout. Computing from the toolbar's real
        // width is required here because the form Padding shrinks the docked panel below its design
        // width, which would leave designer-anchored positions off the right edge.
        UiHelpers.LayoutSearchToolbar(pnlToolbar, txtSearch, lblMatchCount, btnPrev, btnNext, btnClearSearch, rightMargin: 0);

        Font baseFont = rtbHelp.Font;
        _h1Font = new Font(baseFont.FontFamily, baseFont.Size + 6f, FontStyle.Bold);
        _h2Font = new Font(baseFont.FontFamily, baseFont.Size + 4f, FontStyle.Bold);
        _h3Font = new Font(baseFont.FontFamily, baseFont.Size + 2f, FontStyle.Bold);
        _h4Font = new Font(baseFont.FontFamily, baseFont.Size + 0.5f, FontStyle.Bold);
        _boldFont = new Font(baseFont, FontStyle.Bold);
        _italicFont = new Font(baseFont, FontStyle.Italic);
        _monoFont = new Font("Consolas", baseFont.Size);
        // HotTrack is the OS accent (mode-aware); dark mode uses the shared dark link color
        // because HotTrack can render too dim on dark surfaces.
        _linkColor = ThemeColors.IsDarkModeEnabled() ? ThemeColors.LinkDark : SystemColors.HotTrack;

        RenderMarkdown(LoadGuideText());
        _documentText = rtbHelp.Text;
        BuildToc();

        // Open with the first section highlighted and the contents tree focused (not the search
        // box), then reset to the top so the document starts at the beginning - the intro text
        // above the first section stays visible.
        if (tvToc.Nodes.Count > 0)
            tvToc.SelectedNode = tvToc.Nodes[0];
        ActiveControl = tvToc;
        rtbHelp.Select(0, 0);
        rtbHelp.ScrollToCaret();
    }

    // Native surface colors: SystemColors track the active dark/light mode under
    // Application.SetColorMode. Chrome and the read-only document use the dialog surface
    // (Control) so the viewer matches the log viewer; only the editable search box uses the
    // input surface (Window).
    private void ApplyTheme()
    {
        Color surface = SystemColors.Control;
        Color fg = SystemColors.WindowText;

        BackColor = surface;
        pnlToolbar.BackColor = surface;
        splitMain.BackColor = surface;
        tvToc.BackColor = surface;
        tvToc.ForeColor = fg;

        txtSearch.BackColor = SystemColors.Window;
        txtSearch.ForeColor = fg;

        // Accent, matching the log viewer's nav buttons: it reads as a finer, calmer stroke than
        // plain WindowText, which blooms against a dark surface and makes the owner-drawn chevron
        // look heavier than it is.
        foreach (var btn in new[] { btnPrev, btnNext })
        {
            btn.BackColor = surface;
            btn.ForeColor = SystemColors.HotTrack;
            btn.FlatAppearance.BorderColor = SystemColors.ControlDark;
        }

        // Clear button sits inside the search box - blend it in rather than styling it like the
        // nav buttons
        btnClearSearch.BackColor = txtSearch.BackColor;
        btnClearSearch.ForeColor = SystemColors.GrayText;

        lblMatchCount.BackColor = surface;
        lblMatchCount.ForeColor = SystemColors.GrayText;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && !txtSearch.Focused)
        {
            Close();
            e.Handled = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.F)
        {
            txtSearch.Focus();
            txtSearch.SelectAll();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        base.OnKeyDown(e);
    }

    // Returns the shipped guide's markdown, or a short fallback document (rendered through the
    // same pipeline) pointing at the online documentation when the file is missing or unreadable.
    private static string LoadGuideText()
    {
        string path = Path.Combine(AppContext.BaseDirectory, GuideFileName);
        try
        {
            if (File.Exists(path))
                return AppFiles.ReadAllTextShared(path);
        }
        catch (Exception ex)
        {
            LogManager.Instance.LogDebug($"HelpForm.LoadGuideText: {ex.Message}");
        }
        return $"# User guide not available\n\nThe user guide ({GuideFileName}) was not found next to the application.\n\n" +
               $"[Open the online documentation]({AppConstants.GitHubRepoUrl}#readme)";
    }

    // Renders the markdown subset the guide uses into rtbHelp: headings, bullet/numbered lists,
    // blockquotes, tables (re-padded into aligned monospace), fenced code blocks (verbatim
    // monospace), and inline bold/code/links. Badge image lines and horizontal rules carry no
    // prose and are skipped. Anything unrecognised falls through as plain text, so an unhandled
    // construct degrades to readable markdown instead of disappearing.
    private void RenderMarkdown(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];
            if (IsSkippedLine(line)) { i++; continue; }
            if (line.StartsWith("```", StringComparison.Ordinal)) { i = AppendCodeBlock(lines, i + 1); continue; }
            if (line.StartsWith('|')) { i = AppendTable(lines, i); continue; }
            if (line.StartsWith('#')) { AppendHeading(line); i++; continue; }
            if (line.StartsWith("> ", StringComparison.Ordinal)) { AppendQuote(line); i++; continue; }
            AppendBody(line);
            i++;
        }
    }

    // Badge image lines ("[![...") and horizontal rules ("---") carry no prose - headings
    // already delimit the sections visually.
    private static bool IsSkippedLine(string line) =>
        line.StartsWith("[![", StringComparison.Ordinal) || line.Trim() == "---";

    // Positions the caret at the end of the document and applies paragraph formatting for the
    // paragraph about to be appended. SelectionIndent moves the whole paragraph (including
    // wrapped lines) right; SelectionHangingIndent pushes only the wrapped lines further, so a
    // bullet's or list number's text stays aligned past its marker when it wraps. Literal
    // leading spaces cannot do this - RichTextBox wraps back to column 0. Every paragraph type
    // sets both values because new paragraphs inherit the previous paragraph's formatting.
    private void BeginParagraph(int indent, int hangingIndent)
    {
        rtbHelp.SelectionStart = rtbHelp.TextLength;
        rtbHelp.SelectionLength = 0;
        rtbHelp.SelectionIndent = LogicalToDeviceUnits(indent);
        rtbHelp.SelectionHangingIndent = LogicalToDeviceUnits(hangingIndent);
    }

    private void AppendHeading(string line)
    {
        int level = 0;
        while (level < line.Length && line[level] == '#') level++;
        Font font = level switch { 1 => _h1Font!, 2 => _h2Font!, 3 => _h3Font!, _ => _h4Font! };
        BeginParagraph(0, 0);
        string text = line[level..].TrimStart();
        _headings.Add((rtbHelp.TextLength, level, TocText(text)));
        Append(text, font, rtbHelp.ForeColor);
        Append("\n", rtbHelp.Font, rtbHelp.ForeColor);
    }

    // Node label for the contents tree: inline markers stripped and "[text](url)" reduced to
    // its text (the changelog's release headings link to GitHub Releases).
    private static string TocText(string heading)
    {
        string text = StripInlineMarkers(heading);
        while (true)
        {
            int open = text.IndexOf('[');
            if (open < 0) return text;
            int close = text.IndexOf("](", open, StringComparison.Ordinal);
            if (close < 0) return text;
            int end = text.IndexOf(')', close);
            if (end < 0) return text;
            text = text[..open] + text[(open + 1)..close] + text[(end + 1)..];
        }
    }

    // Builds the contents tree from the recorded headings, nesting each heading under the
    // nearest preceding heading of a lower level. A single leading H1 is the document title -
    // the window itself plays that role - so its sections become the tree roots.
    private void BuildToc()
    {
        int first = _headings.Count > 1 && _headings[0].Level == 1 && _headings.Skip(1).All(h => h.Level > 1) ? 1 : 0;
        var parents = new Stack<(int Level, TreeNode Node)>();
        tvToc.BeginUpdate();
        for (int h = first; h < _headings.Count; h++)
        {
            var (charIndex, level, text) = _headings[h];
            while (parents.Count > 0 && parents.Peek().Level >= level)
                parents.Pop();
            var node = new TreeNode(text) { Tag = charIndex };
            if (parents.Count == 0)
                tvToc.Nodes.Add(node);
            else
                parents.Peek().Node.Nodes.Add(node);
            parents.Push((level, node));
        }
        tvToc.EndUpdate();
    }

    private void tvToc_AfterSelect(object? sender, TreeViewEventArgs e)
    {
        if (e.Node?.Tag is int charIndex)
            ScrollToPosition(charIndex);
    }

    // Scrolls so the target lands at the top of the viewport: jump to the document end first,
    // then back to the target - RichTextBox scrolls minimally, and scrolling upward places the
    // caret line at the top instead of the bottom.
    private void ScrollToPosition(int charIndex)
    {
        rtbHelp.Select(rtbHelp.TextLength, 0);
        rtbHelp.ScrollToCaret();
        rtbHelp.Select(charIndex, 0);
        rtbHelp.ScrollToCaret();
    }

    private void AppendQuote(string line)
    {
        BeginParagraph(24, 0);
        AppendInline(line[2..], SystemColors.GrayText);
        Append("\n", rtbHelp.Font, SystemColors.GrayText);
    }

    // Plain paragraph text, bullet items (rendered with a real bullet character), numbered items
    // (kept as-is), and bullet continuation lines (indented source lines under a feature bullet)
    // - all with inline formatting applied and wrap-safe indentation (see BeginParagraph).
    private void AppendBody(string line)
    {
        string trimmed = line.TrimStart();
        int leading = line.Length - trimmed.Length;
        if (trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            BeginParagraph(6 + leading * 7, 11);
            Append("• ", rtbHelp.Font, rtbHelp.ForeColor);
            AppendInline(trimmed[2..], rtbHelp.ForeColor);
        }
        else if (IsNumberedItem(trimmed))
        {
            BeginParagraph(6, 15);
            AppendInline(trimmed, rtbHelp.ForeColor);
        }
        else
        {
            // Indented continuation of a bullet aligns under the bullet's text; plain paragraphs
            // sit at the margin.
            BeginParagraph(leading > 0 ? 17 : 0, 0);
            AppendInline(trimmed, rtbHelp.ForeColor);
        }
        Append("\n", rtbHelp.Font, rtbHelp.ForeColor);
    }

    // True for "N. text" ordered-list items (one or two digits - the guide's lists stay short).
    private static bool IsNumberedItem(string trimmed)
    {
        int dot = trimmed.IndexOf('.');
        return dot is 1 or 2
            && dot + 1 < trimmed.Length && trimmed[dot + 1] == ' '
            && int.TryParse(trimmed[..dot], out _);
    }

    // Appends the fenced block's lines verbatim in monospace and returns the index after the
    // closing fence. Covers the install command and the release-workflow diagram.
    private int AppendCodeBlock(string[] lines, int start)
    {
        int i = start;
        for (; i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal); i++)
        {
            BeginParagraph(6, 0);
            Append(lines[i] + "\n", _monoFont!, rtbHelp.ForeColor);
        }
        return i + 1; // skip the closing fence
    }

    // Renders a markdown table as a definition list instead of a grid: each data row becomes a
    // bold first-cell line with an indented "Header: value" line per remaining cell. A padded
    // monospace grid was tried first, but the settings tables' description cells push rows far
    // past any window width and RichTextBox word-wrap folds such a grid into interleaved
    // fragments (there is no per-paragraph no-wrap). The definition list wraps gracefully at
    // every width and lets cell contents keep their inline formatting.
    private int AppendTable(string[] lines, int start)
    {
        var rows = new List<string[]>();
        int i = start;
        for (; i < lines.Length && lines[i].StartsWith('|'); i++)
        {
            string[] cells = lines[i].Trim('|').Split('|');
            for (int c = 0; c < cells.Length; c++)
                cells[c] = cells[c].Trim();
            if (!IsSeparatorRow(cells))
                rows.Add(cells);
        }
        if (rows.Count < 2) return i; // header only (or nothing) - no data to show

        string[] headers = rows[0];
        for (int r = 1; r < rows.Count; r++)
        {
            string[] row = rows[r];
            BeginParagraph(6, 0);
            Append(StripInlineMarkers(row[0]), _boldFont!, rtbHelp.ForeColor);
            Append("\n", rtbHelp.Font, rtbHelp.ForeColor);
            for (int c = 1; c < row.Length && c < headers.Length; c++)
            {
                BeginParagraph(24, 0);
                Append(StripInlineMarkers(headers[c]) + ": ", rtbHelp.Font, SystemColors.GrayText);
                AppendInline(row[c], rtbHelp.ForeColor);
                Append("\n", rtbHelp.Font, rtbHelp.ForeColor);
            }
            if (r < rows.Count - 1) // separator between rows; the source's own blank line follows the table
                Append("\n", rtbHelp.Font, rtbHelp.ForeColor);
        }
        return i;
    }

    private static bool IsSeparatorRow(string[] cells) =>
        cells.All(c => c.Length > 0 && c.Trim(':').All(ch => ch == '-'));

    private static string StripInlineMarkers(string text) =>
        text.Replace("**", string.Empty).Replace("`", string.Empty);

    // Appends one line's text with inline markdown applied: [text](url) as a clickable link,
    // **text** in bold, *text* in italic, `text` in monospace. Escaping is not supported - the
    // guide does not use it - and an unterminated marker falls through as literal text.
    private void AppendInline(string text, Color color)
    {
        int pos = 0;
        while (pos < text.Length)
        {
            int link = text.IndexOf('[', pos);
            int star = text.IndexOf('*', pos); // "**" (bold) or "*" (italic) - disambiguated below
            int code = text.IndexOf('`', pos);
            int next = MinMarker(link, star, code);
            if (next < 0)
            {
                Append(text[pos..], rtbHelp.Font, color);
                return;
            }
            if (next > pos)
                Append(text[pos..next], rtbHelp.Font, color);

            int consumed;
            if (next == link)
                consumed = TryAppendLink(text, next);
            else if (next == star)
                consumed = next + 1 < text.Length && text[next + 1] == '*'
                    ? TryAppendSpan(text, next, "**", _boldFont!, color)
                    : TryAppendSpan(text, next, "*", _italicFont!, color);
            else
                consumed = TryAppendSpan(text, next, "`", _monoFont!, color);
            if (consumed == 0)
            {
                // Unterminated or malformed marker - emit one literal character and move on.
                Append(text[next].ToString(), rtbHelp.Font, color);
                consumed = 1;
            }
            pos = next + consumed;
        }
    }

    // Smallest non-negative marker index, or -1 when none was found.
    private static int MinMarker(int a, int b, int c)
    {
        int min = -1;
        foreach (int v in (ReadOnlySpan<int>)[a, b, c])
        {
            if (v >= 0 && (min < 0 || v < min)) min = v;
        }
        return min;
    }

    // Appends a "[text](url)" link at 'start' as styled text and records its character range for
    // click handling. Returns the source characters consumed, or 0 if the syntax does not match.
    private int TryAppendLink(string text, int start)
    {
        int closeBracket = text.IndexOf(']', start + 1);
        if (closeBracket < 0 || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(')
            return 0;
        int closeParen = text.IndexOf(')', closeBracket + 2);
        if (closeParen < 0)
            return 0;

        string linkText = text[(start + 1)..closeBracket];
        string url = text[(closeBracket + 2)..closeParen];
        _links.Add((rtbHelp.TextLength, linkText.Length, url));
        rtbHelp.SelectionStart = rtbHelp.TextLength;
        rtbHelp.SelectionFont = rtbHelp.Font;
        rtbHelp.SelectionColor = _linkColor;
        // Underline via a derived font would need per-link disposal tracking; color alone reads
        // as a link here because the accent is used for nothing else in the document.
        rtbHelp.AppendText(linkText);
        return closeParen - start + 1;
    }

    // Appends a "<marker>text<marker>" span at 'start' with the given font. Returns the source
    // characters consumed, or 0 if the closing marker is missing.
    private int TryAppendSpan(string text, int start, string marker, Font font, Color color)
    {
        int contentStart = start + marker.Length;
        int end = text.IndexOf(marker, contentStart, StringComparison.Ordinal);
        if (end < 0)
            return 0;
        Append(text[contentStart..end], font, color);
        return end + marker.Length - start;
    }

    private void Append(string text, Font font, Color color)
    {
        if (text.Length == 0) return;
        rtbHelp.SelectionStart = rtbHelp.TextLength;
        rtbHelp.SelectionFont = font;
        rtbHelp.SelectionColor = color;
        rtbHelp.AppendText(text);
    }

    // Triggered when the search text changes - shows/hides the clear button and rescans.
    private void txtSearch_TextChanged(object? sender, EventArgs e)
    {
        btnClearSearch.Visible = txtSearch.Text.Length > 0;
        RefreshSearch();
    }

    // Handles Enter (next), Shift+Enter (prev), and Escape (clear) in the search box
    private void txtSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            if (e.Shift) SearchPrev(); else SearchNext();
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            txtSearch.Clear();
            e.SuppressKeyPress = true;
        }
    }

    private void btnClearSearch_Click(object? sender, EventArgs e) => txtSearch.Clear();
    private void btnPrev_Click(object? sender, EventArgs e) => SearchPrev();
    private void btnNext_Click(object? sender, EventArgs e) => SearchNext();

    // Rescans the document for the current query, rebuilds the match list, and navigates to the
    // first match. The current match is shown via the selection (HideSelection is false, so it
    // stays visible while the search box has focus).
    private void RefreshSearch()
    {
        _searchMatches.Clear();
        _searchIndex = -1;

        string query = txtSearch.Text;
        if (query.Length == 0)
        {
            lblMatchCount.Text = string.Empty;
            rtbHelp.SelectionLength = 0; // drop the leftover match highlight
            return;
        }

        int start = 0;
        while (true)
        {
            int found = _documentText.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;
            _searchMatches.Add(found);
            start = found + 1;
        }

        if (_searchMatches.Count == 0)
        {
            lblMatchCount.Text = "0 / 0";
            rtbHelp.SelectionLength = 0;
            return;
        }
        NavigateToMatch(0);
    }

    private void SearchNext()
    {
        if (_searchMatches.Count == 0) return;
        NavigateToMatch((_searchIndex + 1) % _searchMatches.Count);
    }

    private void SearchPrev()
    {
        if (_searchMatches.Count == 0) return;
        NavigateToMatch(_searchIndex <= 0 ? _searchMatches.Count - 1 : _searchIndex - 1);
    }

    private void NavigateToMatch(int index)
    {
        _searchIndex = index;
        rtbHelp.Select(_searchMatches[index], txtSearch.Text.Length);
        rtbHelp.ScrollToCaret();
        lblMatchCount.Text = $"{_searchIndex + 1} / {_searchMatches.Count}";
    }

    // Paints the clear button's X via the shared drawer, for the same reason the chevrons are
    // drawn: exact weight and centering, and no dependency on a font glyph.
    private void ClearButton_Paint(object? sender, PaintEventArgs e)
    {
        if (sender is Button btn) UiHelpers.DrawClearGlyph(btn, e.Graphics);
    }

    // Paints the search-nav chevrons (btnPrev points up) via the shared drawer.
    private void NavButton_Paint(object? sender, PaintEventArgs e)
    {
        if (sender is not Button btn) return;
        UiHelpers.DrawNavChevron(btn, e.Graphics, up: btn == btnPrev);
    }

    // Enable "Copy" only when there is a selection; "Copy All" and "Select All" always apply.
    private void ctxHelp_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        => ctxHelpCopy.Enabled = rtbHelp.SelectionLength > 0;

    private void ctxHelpCopy_Click(object? sender, EventArgs e)
    {
        if (rtbHelp.SelectionLength > 0)
            UiHelpers.SetClipboardTextSafely(rtbHelp.SelectedText);
    }

    private void ctxHelpCopyAll_Click(object? sender, EventArgs e) => UiHelpers.SetClipboardTextSafely(rtbHelp.Text);

    private void ctxHelpSelectAll_Click(object? sender, EventArgs e) => rtbHelp.SelectAll();

    private void rtbHelp_MouseUp(object? sender, MouseEventArgs e) // NOSONAR S2325 - calls the instance method LinkUrlAtPoint, cannot be static
    {
        if (e.Button != MouseButtons.Left) return;
        string? url = LinkUrlAtPoint(e.Location);
        if (url is not null)
            UiHelpers.OpenUrl(url);
    }

    private void rtbHelp_MouseMove(object? sender, MouseEventArgs e) =>
        rtbHelp.Cursor = LinkUrlAtPoint(e.Location) is not null ? Cursors.Hand : Cursors.IBeam;

    // Resolves the link under a client point, or null. GetCharIndexFromPosition returns the
    // NEAREST character, so a bare range check would light up clicks in the blank space past a
    // line that ends in a link; the geometric check against the link's own start/end positions
    // rejects those (a link spanning a wrap line would merely miss - none in practice do).
    private string? LinkUrlAtPoint(Point pt)
    {
        if (_links.Count == 0) return null;
        int index = rtbHelp.GetCharIndexFromPosition(pt);
        foreach (var (start, length, url) in _links)
        {
            if (index < start || index >= start + length) continue;
            Point startPos = rtbHelp.GetPositionFromCharIndex(start);
            Point endPos = rtbHelp.GetPositionFromCharIndex(start + length);
            int lineHeight = rtbHelp.Font.Height;
            bool inside = pt.Y >= startPos.Y && pt.Y <= startPos.Y + lineHeight
                       && pt.X >= startPos.X - 2 && (endPos.Y > startPos.Y || pt.X <= endPos.X + 2);
            return inside ? url : null;
        }
        return null;
    }
}
