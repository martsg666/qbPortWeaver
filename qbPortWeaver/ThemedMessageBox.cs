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
        private const int EdgeMargin = 16;
        private const int BottomMargin = 12;
        private const int IconSize = 32;
        private const int IconTextGap = 12;
        private const int ButtonGap = 8;
        private const int ButtonWidth = 82;
        private const int ButtonHeight = 28;
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
            // Match the designer forms' 96-DPI autoscale baseline so the manually-placed controls scale.
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;

            _sound = SoundFor(icon);
            BuildLayout(text, icon, buttons);
        }

        private void BuildLayout(string text, MessageBoxIcon icon, MessageBoxButtons buttons)
        {
            // Icon (optional). SystemIcons are shared statics - never dispose them; ToBitmap() makes a
            // copy we own, disposed with the form.
            Icon? sysIcon = IconFor(icon);
            if (sysIcon is not null)
            {
                _iconBitmap = sysIcon.ToBitmap();
                Controls.Add(new PictureBox
                {
                    Image = _iconBitmap,
                    Size = new Size(IconSize, IconSize),
                    Location = new Point(EdgeMargin, EdgeMargin),
                    SizeMode = PictureBoxSizeMode.Zoom,
                });
            }

            int textLeft = sysIcon is not null ? EdgeMargin + IconSize + IconTextGap : EdgeMargin;
            var lbl = new Label
            {
                Text = text,
                AutoSize = true,
                UseMnemonic = false, // render a literal '&' in messages instead of a mnemonic underline
                MaximumSize = new Size(MessageMaxWidth, 0),
                Location = new Point(textLeft, EdgeMargin),
            };
            Controls.Add(lbl);

            // Content bottom = lower of the icon and the (possibly wrapped, multi-line) message.
            int contentBottom = Math.Max(sysIcon is not null ? EdgeMargin + IconSize : 0, lbl.Bottom);

            var specs = ButtonSpecs(buttons);
            int buttonsWidth = specs.Length * ButtonWidth + (specs.Length - 1) * ButtonGap;
            int clientWidth = Math.Max(lbl.Right + EdgeMargin, buttonsWidth + 2 * EdgeMargin);
            int buttonTop = contentBottom + EdgeMargin;

            Button? accept = null, cancel = null;
            int x = clientWidth - EdgeMargin - buttonsWidth; // right-aligned button group
            foreach (var (btnText, result, isDefault, isCancel) in specs)
            {
                var b = new Button
                {
                    Text = btnText,
                    DialogResult = result,
                    Size = new Size(ButtonWidth, ButtonHeight),
                    Location = new Point(x, buttonTop),
                };
                Controls.Add(b);
                if (isDefault) accept = b;
                if (isCancel) cancel = b;
                x += ButtonWidth + ButtonGap;
            }
            AcceptButton = accept;   // Enter triggers the default (affirmative) button
            CancelButton = cancel;   // Escape triggers the cancel-like button, or nothing for Yes/No

            ClientSize = new Size(clientWidth, buttonTop + ButtonHeight + BottomMargin);
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
