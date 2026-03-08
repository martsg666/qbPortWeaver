namespace qbPortWeaver
{
    partial class LogViewerForm
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.RichTextBox rtbLog;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _watcher?.Dispose();
                components?.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            rtbLog = new System.Windows.Forms.RichTextBox();
            SuspendLayout();

            // rtbLog
            rtbLog.BackColor        = System.Drawing.SystemColors.Window;
            rtbLog.BorderStyle      = System.Windows.Forms.BorderStyle.None;
            rtbLog.Dock             = System.Windows.Forms.DockStyle.Fill;
            rtbLog.Font             = new System.Drawing.Font("Consolas", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            rtbLog.Location         = new System.Drawing.Point(0, 0);
            rtbLog.ReadOnly         = true;
            rtbLog.DetectUrls       = false;
            rtbLog.ScrollBars       = System.Windows.Forms.RichTextBoxScrollBars.Both;
            rtbLog.ShortcutsEnabled = true;
            rtbLog.Size             = new System.Drawing.Size(1100, 560);
            rtbLog.TabIndex         = 0;
            rtbLog.WordWrap         = false;

            // LogViewerForm
            AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            AutoScaleMode       = System.Windows.Forms.AutoScaleMode.Font;
            ClientSize          = new System.Drawing.Size(1100, 560);
            Controls.Add(rtbLog);
            MinimumSize         = new System.Drawing.Size(500, 300);
            Name                = "LogViewerForm";
            ShowIcon            = false;
            ShowInTaskbar       = true;
            StartPosition       = System.Windows.Forms.FormStartPosition.CenterScreen;
            Text                = $"{AppConstants.AppName} | Log Viewer";

            ResumeLayout(false);
        }
    }
}
