namespace qbPortWeaver;

partial class HelpForm
{
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Dispose explicitly created fonts (WinForms controls do not own their Font)
            rtbHelp?.Font?.Dispose();
            _h1Font?.Dispose();
            _h2Font?.Dispose();
            _h3Font?.Dispose();
            _h4Font?.Dispose();
            _boldFont?.Dispose();
            _italicFont?.Dispose();
            _monoFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        rtbHelp = new RichTextBox();
        SuspendLayout();

        // rtbHelp - read-only rendered view of the shipped user guide. Content is written once
        // in OnLoad by RenderMarkdown; links are tracked by character range (see _links).
        rtbHelp.BackColor   = SystemColors.Window;
        rtbHelp.BorderStyle = BorderStyle.None;
        rtbHelp.Dock        = DockStyle.Fill;
        rtbHelp.Font        = new Font("Segoe UI", 9.75F);
        rtbHelp.ReadOnly    = true;
        rtbHelp.ScrollBars  = RichTextBoxScrollBars.Vertical;
        rtbHelp.TabIndex    = 0;
        rtbHelp.Text        = "";
        rtbHelp.MouseUp    += rtbHelp_MouseUp;
        rtbHelp.MouseMove  += rtbHelp_MouseMove;

        // HelpForm
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode       = AutoScaleMode.Font;
        ClientSize          = new Size(780, 640);
        Controls.Add(rtbHelp);
        Icon                = Properties.Resources.qbPortWeaver;
        MinimumSize         = new Size(480, 360);
        Name                = "HelpForm";
        Padding             = new Padding(12, 8, 4, 8); // right stays slim so the scrollbar hugs the edge
        ShowIcon            = true;
        ShowInTaskbar       = true;
        StartPosition       = FormStartPosition.CenterScreen;
        Text                = "qbPortWeaver | Help"; // overridden in OnLoad with AppIdentity.AppName

        ResumeLayout(false);
    }

    private RichTextBox rtbHelp;
}
