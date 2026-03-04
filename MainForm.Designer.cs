namespace qbPortWeaver
{
    partial class frmMain
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose components container
                if (components != null)
                    components.Dispose();

                // Dispose custom resources
                try
                {
                    _trayIcon?.Dispose();
                    _trayMenu?.Dispose();
                    _iconBase?.Dispose();
                    _iconOK?.Dispose();
                    _iconWarning?.Dispose();
                    _iconError?.Dispose();
                    _updateCheckTimer?.Dispose();
                    _updateSemaphore?.Dispose();
                    _delayCancel?.Dispose();
                    _shutdownCts?.Dispose();
                }
                catch (Exception ex)
                {
                    LogManager.Instance?.LogDebug($"frmMain.Dispose: Error disposing resources: {ex.Message}");
                }
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            SuspendLayout();
            //
            // frmMain
            //
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 450);
            Icon = Properties.Resources.qbPortWeaver;
            Name = "frmMain";
            Text = "qbPortWeaver";
            Load += frmMain_Load;
            ResumeLayout(false);
        }

        #endregion
    }
}
