namespace qbPortWeaver;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _delayCts?.Cancel();
            _delayCts?.Dispose();
            _shutdownCts?.Cancel();
            _shutdownCts?.Dispose();

            _trayIcon?.Dispose();
            _trayMenu?.Dispose();
            _iconBase?.Dispose();
            _iconOk?.Dispose();
            _iconWarning?.Dispose();
            _iconError?.Dispose();
            _updateCheckTimer?.Dispose();
            _updateSemaphore?.Dispose();
            _updateMenuItemFont?.Dispose();
            components?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        SuspendLayout();
        // ── MainForm ──────────────────────────────────────────────────
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1, 1);
        Icon = Properties.Resources.qbPortWeaver;
        Name = "MainForm";
        ShowInTaskbar = false;
        Text = "qbPortWeaver";
        Load += MainForm_Load;
        ResumeLayout(false);
    }
}
