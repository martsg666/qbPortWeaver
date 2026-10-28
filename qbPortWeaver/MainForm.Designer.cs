namespace qbPortWeaver;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // _delayCts and _updateSemaphore are cancelled or left, not disposed: the main loop is not
            // awaited on exit and may still be unwinding a cancelled cycle on a background thread, where a
            // disposed one turns an ordinary shutdown into Error lines. Both live as long as the process.
            // _shutdownCts is disposed, after Cancel: the loop uses only the token cached in _shutdownToken.
            _delayCts?.Cancel();
            _shutdownCts?.Cancel();
            _shutdownCts?.Dispose();

            _resyncDebounceTimer?.Dispose();

            _trayIcon?.Dispose();
            _trayMenu?.Dispose();
            _iconBase?.Dispose();
            _iconOk?.Dispose();
            _iconWarning?.Dispose();
            _iconError?.Dispose();
            _iconPaused?.Dispose();
            _updateCheckTimer?.Dispose();
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
