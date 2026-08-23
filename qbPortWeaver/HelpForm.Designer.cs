namespace qbPortWeaver;

partial class HelpForm
{
    private System.ComponentModel.IContainer components;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Dispose explicitly created fonts (WinForms controls do not own their Font)
            rtbHelp?.Font?.Dispose();
            lblMatchCount?.Font?.Dispose();
            _h1Font?.Dispose();
            _h2Font?.Dispose();
            _h3Font?.Dispose();
            _h4Font?.Dispose();
            _boldFont?.Dispose();
            _italicFont?.Dispose();
            _monoFont?.Dispose();

            components?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components     = new System.ComponentModel.Container();
        toolTip        = new ToolTip(components);
        pnlToolbar     = new Panel();
        txtSearch      = new PlaceholderTextBox();
        btnClearSearch = new Button();
        lblMatchCount  = new Label();
        btnPrev        = new Button();
        btnNext        = new Button();
        splitMain      = new SplitContainer();
        tvToc          = new TreeView();
        rtbHelp        = new RichTextBox();
        ctxHelp          = new ContextMenuStrip(components);
        ctxHelpCopy      = new ToolStripMenuItem();
        ctxHelpCopyAll   = new ToolStripMenuItem();
        ctxHelpSelectAll = new ToolStripMenuItem();
        SuspendLayout();

        // Search controls - anchored Right so they stay at the toolbar's right edge on resize,
        // matching the log viewer. Layout from right: [4] [btnNext:26] [btnPrev:26] [4]
        // [lblMatchCount:64] [4] [txtSearch:220]. All positions here are placeholders - the
        // real layout runs in OnLoad against the toolbar's actual docked width (the form
        // Padding makes the design-time width unreliable for right-anchored children).
        // btnClearSearch floats inside the right edge of txtSearch (z-order above it).
        AnchorStyles rightAnchor = AnchorStyles.Right | AnchorStyles.Top;

        // txtSearch
        txtSearch.Anchor          = rightAnchor;
        txtSearch.BorderStyle     = BorderStyle.FixedSingle;
        txtSearch.Location        = new Point(416, 8);
        txtSearch.PlaceholderText = "Search…";
        txtSearch.Width           = 220;
        txtSearch.TabIndex        = 1;
        txtSearch.TextChanged    += txtSearch_TextChanged;
        txtSearch.KeyDown        += txtSearch_KeyDown;
        toolTip.SetToolTip(txtSearch, "Search the guide (Ctrl+F)");

        // btnClearSearch - overlays the right interior of txtSearch; sized and positioned in OnLoad
        btnClearSearch.Anchor                    = rightAnchor;
        btnClearSearch.FlatStyle                 = FlatStyle.Flat;
        btnClearSearch.FlatAppearance.BorderSize = 0;
        btnClearSearch.Location                  = new Point(614, 11);
        btnClearSearch.Padding                   = new Padding(0);
        btnClearSearch.Size                      = new Size(16, 16);
        btnClearSearch.Text                      = ""; // clear glyph owner-drawn in ClearButton_Paint
        btnClearSearch.TextAlign                 = ContentAlignment.MiddleCenter;
        btnClearSearch.TabIndex                  = 2;
        btnClearSearch.TabStop                   = false;
        btnClearSearch.Visible                   = false;
        btnClearSearch.Click                    += btnClearSearch_Click;
        btnClearSearch.Paint                    += ClearButton_Paint;
        toolTip.SetToolTip(btnClearSearch, "Clear search");

        // lblMatchCount
        lblMatchCount.Anchor    = rightAnchor;
        lblMatchCount.AutoSize  = false;
        lblMatchCount.Font      = new Font("Segoe UI", 8F);
        lblMatchCount.Location  = new Point(640, 11);
        lblMatchCount.Size      = new Size(64, 14);
        lblMatchCount.TextAlign = ContentAlignment.MiddleLeft;

        // btnPrev
        btnPrev.Anchor    = rightAnchor;
        btnPrev.FlatStyle = FlatStyle.Flat;
        btnPrev.Location  = new Point(708, 5);
        btnPrev.Size      = new Size(26, 26);
        btnPrev.TabIndex  = 3;
        btnPrev.Text      = ""; // up chevron owner-drawn in NavButton_Paint
        btnPrev.Click    += btnPrev_Click;
        btnPrev.Paint    += NavButton_Paint;
        toolTip.SetToolTip(btnPrev, "Previous match");

        // btnNext
        btnNext.Anchor    = rightAnchor;
        btnNext.FlatStyle = FlatStyle.Flat;
        btnNext.Location  = new Point(734, 5);
        btnNext.Size      = new Size(26, 26);
        btnNext.TabIndex  = 4;
        btnNext.Text      = ""; // down chevron owner-drawn in NavButton_Paint
        btnNext.Click    += btnNext_Click;
        btnNext.Paint    += NavButton_Paint;
        toolTip.SetToolTip(btnNext, "Next match");

        // pnlToolbar - Width must match the docked width (ClientSize minus the form Padding)
        // before right-anchored children are added, otherwise the default panel width produces
        // a negative right-margin and the search group flies off-screen when the panel expands.
        pnlToolbar.Controls.AddRange(new Control[] {
            btnClearSearch, txtSearch, lblMatchCount, btnPrev, btnNext });
        pnlToolbar.Dock     = DockStyle.Top;
        pnlToolbar.Size     = new Size(764, 36);
        pnlToolbar.TabIndex = 0;

        // tvToc - table of contents built from the guide's headings (see BuildToc); selecting a
        // node scrolls the document to that heading.
        tvToc.BorderStyle   = BorderStyle.None;
        tvToc.Dock          = DockStyle.Fill;
        tvToc.HideSelection = false;
        tvToc.FullRowSelect = true;
        tvToc.TabIndex      = 0;
        tvToc.AfterSelect  += tvToc_AfterSelect;

        // rtbHelp - read-only rendered view of the shipped user guide. Content is written once
        // in OnLoad by RenderMarkdown; links are tracked by character range (see _links).
        // HideSelection stays false so the current search match remains visible while the
        // search box has focus. Like the log viewer's read-only log, the document uses the
        // dialog surface (Control); only the editable search box uses the input surface.
        rtbHelp.BackColor     = SystemColors.Control;
        rtbHelp.BorderStyle   = BorderStyle.None;
        rtbHelp.Dock          = DockStyle.Fill;
        rtbHelp.Font          = new Font("Segoe UI", 9.75F);
        rtbHelp.HideSelection = false;
        rtbHelp.ReadOnly      = true;
        rtbHelp.ScrollBars    = RichTextBoxScrollBars.Vertical;
        rtbHelp.TabIndex      = 1;
        rtbHelp.Text          = "";
        rtbHelp.MouseUp      += rtbHelp_MouseUp;
        rtbHelp.MouseMove    += rtbHelp_MouseMove;

        // Right-click context menu on the read-only document: copy the selection, copy the whole
        // guide, or select all (mirrors the log viewer's context menu).
        ctxHelpCopy.Text      = "Copy";
        ctxHelpCopyAll.Text   = "Copy All";
        ctxHelpSelectAll.Text = "Select All";
        ctxHelpCopy.Click      += ctxHelpCopy_Click;
        ctxHelpCopyAll.Click   += ctxHelpCopyAll_Click;
        ctxHelpSelectAll.Click += ctxHelpSelectAll_Click;
        ctxHelp.Items.AddRange(new ToolStripItem[] { ctxHelpCopy, ctxHelpCopyAll, ctxHelpSelectAll });
        ctxHelp.Opening += ctxHelp_Opening;
        rtbHelp.ContextMenuStrip = ctxHelp;

        // splitMain - contents tree on the left (fixed on resize), document on the right.
        // Size must be set before SplitterDistance so the distance is valid against the min sizes.
        splitMain.Dock          = DockStyle.Fill;
        splitMain.FixedPanel    = FixedPanel.Panel1;
        splitMain.Size          = new Size(764, 588);
        splitMain.Panel1MinSize = 120;
        splitMain.Panel2MinSize = 240;
        splitMain.SplitterDistance = 190;
        splitMain.SplitterWidth = 6;
        splitMain.TabIndex      = 1;
        splitMain.Panel1.Controls.Add(tvToc);
        splitMain.Panel2.Controls.Add(rtbHelp);
        splitMain.Panel2.Padding = new Padding(8, 0, 0, 0); // gap between splitter and document text

        // HelpForm
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode       = AutoScaleMode.Font;
        ClientSize          = new Size(780, 640);
        Controls.Add(splitMain);  // fill control first so it lays out after the docked toolbar
        Controls.Add(pnlToolbar);
        Icon                = Properties.Resources.qbPortWeaver;
        // Static designer minimum (DPI-scaled by AutoScaleMode.Font), the pattern shared by all
        // sizable forms. Width clears the SplitContainer's panel minimums (120 + 240 + 6) and the
        // toolbar search group; height keeps the split and toolbar usable.
        MinimumSize         = new Size(480, 360);
        Name                = "HelpForm";
        Padding             = new Padding(12, 8, 4, 8); // right stays slim so the scrollbar hugs the edge
        ShowIcon            = true;
        ShowInTaskbar       = true;
        StartPosition       = FormStartPosition.CenterScreen;
        Text                = "qbPortWeaver | Help"; // overridden in OnLoad with AppIdentity.AppName

        ResumeLayout(false);
    }

    private ToolTip        toolTip;
    private Panel          pnlToolbar;
    private PlaceholderTextBox txtSearch;
    private Button         btnClearSearch;
    private Label          lblMatchCount;
    private Button         btnPrev;
    private Button         btnNext;
    private SplitContainer splitMain;
    private TreeView       tvToc;
    private RichTextBox    rtbHelp;
    private ContextMenuStrip  ctxHelp;
    private ToolStripMenuItem ctxHelpCopy;
    private ToolStripMenuItem ctxHelpCopyAll;
    private ToolStripMenuItem ctxHelpSelectAll;
}
