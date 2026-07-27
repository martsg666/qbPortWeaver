using System.Media;

namespace qbPortWeaver;

/// <summary>
/// Theme-aware replacement for <see cref="MessageBox"/>. The native MessageBox is drawn by the OS and
/// ignores <see cref="Application.SetColorMode"/>, so it is always light - jarring in dark mode. This
/// managed dialog uses <see cref="SystemColors"/>, which follow the app's color mode, so confirmations
/// and alerts match the rest of the app. <see cref="Show"/> mirrors <see cref="MessageBox.Show(string,
/// string, MessageBoxButtons, MessageBoxIcon)"/> so call sites swap directly. Built in code (no designer)
/// because the button set and message size are dynamic - the same justified exception as ClientChooserForm.
/// </summary>
internal static class ThemedMessageBox
{
    /// <summary>Shows a themed modal message dialog and returns the <see cref="DialogResult"/> of the
    /// button the user clicked. Mirrors <see cref="MessageBox.Show(string, string, MessageBoxButtons,
    /// MessageBoxIcon)"/>.</summary>
    public static DialogResult Show(string text, string caption = "",
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        using var form = new ThemedMessageBoxForm(text, caption, buttons, icon);
        var owner = Form.ActiveForm;
        // Centre on the active window when there is one (and it is not this dialog), else on screen.
        return owner is not null && !owner.IsDisposed && owner != form
            ? form.ShowDialog(owner)
            : form.ShowDialog();
    }

    private sealed class ThemedMessageBoxForm : Form
    {
        private const int IconSize = 32;
        private const int IconTextGap = 12;
        private const int MessageMaxWidth = 360;

        private readonly SystemSound? _sound;
        private Bitmap? _iconBitmap;

        internal ThemedMessageBoxForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            Text = caption;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            // The layout containers size the form; AutoScaleMode.Font (designer baseline) scales fonts.
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            _sound = SoundFor(icon);
            BuildLayout(text, icon, buttons);
        }

        private void BuildLayout(string text, MessageBoxIcon icon, MessageBoxButtons buttons)
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(DialogLayout.EdgeMargin, DialogLayout.EdgeMargin, DialogLayout.EdgeMargin, DialogLayout.BottomMargin),
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // content
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons
            root.Controls.Add(BuildContent(text, icon), 0, 0);

            var specs = ButtonSpecs(buttons);
            var buttonControls = new Button[specs.Length];
            for (int i = 0; i < specs.Length; i++)
            {
                var b = DialogLayout.DialogButton(specs[i].Text, specs[i].Result);
                if (specs[i].IsDefault) AcceptButton = b; // Enter triggers the default (affirmative) button
                if (specs[i].IsCancel) CancelButton = b;  // Escape triggers the cancel-like button, or nothing for Yes/No
                buttonControls[i] = b;
            }
            root.Controls.Add(DialogLayout.ButtonRow(buttonControls), 0, 1);

            Controls.Add(root);
        }

        // Builds the content row: the message label, preceded by the severity icon when there is one.
        private Control BuildContent(string text, MessageBoxIcon icon)
        {
            var lbl = new Label
            {
                Text = text,
                AutoSize = true,
                UseMnemonic = false, // render a literal '&' in messages instead of a mnemonic underline
                MaximumSize = new Size(MessageMaxWidth, 0),
                Margin = new Padding(0),
            };

            // SystemIcons are shared statics - never dispose them; ToBitmap() makes a copy we own,
            // disposed with the form.
            Icon? sysIcon = IconFor(icon);
            if (sysIcon is null)
                return lbl;

            _iconBitmap = sysIcon.ToBitmap();
            var content = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0),
            };
            content.Controls.Add(new PictureBox
            {
                Image = _iconBitmap,
                Size = new Size(IconSize, IconSize),
                SizeMode = PictureBoxSizeMode.Zoom,
                Margin = new Padding(0, 0, IconTextGap, 0),
            }, 0, 0);
            lbl.Anchor = AnchorStyles.Left; // vertically center a short message against the icon
            content.Controls.Add(lbl, 1, 0);
            return content;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _sound?.Play(); // match the native MessageBox's per-icon system sound
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _iconBitmap?.Dispose();
            base.Dispose(disposing);
        }

        // The standard system icons - the same glyphs the native MessageBox uses; they read correctly on
        // both light and dark surfaces. MessageBoxIcon aliases (Hand/Stop=Error, Exclamation=Warning,
        // Asterisk=Information) share enum values, so the canonical cases cover them.
        private static Icon? IconFor(MessageBoxIcon icon) => icon switch
        {
            MessageBoxIcon.Error => SystemIcons.Error,
            MessageBoxIcon.Warning => SystemIcons.Warning,
            MessageBoxIcon.Information => SystemIcons.Information,
            MessageBoxIcon.Question => SystemIcons.Question,
            _ => null,
        };

        private static SystemSound? SoundFor(MessageBoxIcon icon) => icon switch
        {
            MessageBoxIcon.Error => SystemSounds.Hand,
            MessageBoxIcon.Warning => SystemSounds.Exclamation,
            MessageBoxIcon.Information => SystemSounds.Asterisk,
            MessageBoxIcon.Question => SystemSounds.Question,
            _ => null,
        };

        // One spec per button, in left-to-right display order (affirmative first, dismiss last - matching
        // the app's button convention and the native MessageBox). IsDefault -> AcceptButton (Enter);
        // IsCancel -> CancelButton (Escape). Yes/No has no cancel button, so Escape does nothing there.
        private static (string Text, DialogResult Result, bool IsDefault, bool IsCancel)[] ButtonSpecs(MessageBoxButtons buttons) => buttons switch
        {
            MessageBoxButtons.OKCancel => [("OK", DialogResult.OK, true, false), ("Cancel", DialogResult.Cancel, false, true)],
            MessageBoxButtons.YesNo => [("Yes", DialogResult.Yes, true, false), ("No", DialogResult.No, false, false)],
            MessageBoxButtons.YesNoCancel => [("Yes", DialogResult.Yes, true, false), ("No", DialogResult.No, false, false), ("Cancel", DialogResult.Cancel, false, true)],
            MessageBoxButtons.RetryCancel => [("Retry", DialogResult.Retry, true, false), ("Cancel", DialogResult.Cancel, false, true)],
            MessageBoxButtons.AbortRetryIgnore => [("Abort", DialogResult.Abort, false, false), ("Retry", DialogResult.Retry, true, false), ("Ignore", DialogResult.Ignore, false, false)],
            _ => [("OK", DialogResult.OK, true, true)], // OK (and any unsupported set): single default+cancel button
        };
    }
}
