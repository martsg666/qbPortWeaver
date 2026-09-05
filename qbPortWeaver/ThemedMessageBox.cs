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
    /// <summary>An informational dialog with an OK button.</summary>
    /// <remarks>The shorthands below exist because every dialog in the app passes the same caption -
    /// <see cref="AppIdentity.AppName"/> - with a fixed button/icon pair. Spelling that triple out
    /// repeated it at every call site and left the caption convention resting on each author
    /// remembering it; here the convention is the signature. Deliberately only the pairs that have a
    /// caller: an unused shorthand is dead code no compiler warns about. Reach for
    /// <see cref="Show"/> directly for anything they do not cover - today that is the media import's
    /// runtime-chosen icon and the one dialog with its own caption.</remarks>
    internal static void Info(string text) =>
        Show(text, AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);

    /// <summary>A warning dialog with an OK button: something the user should know, but nothing was lost.</summary>
    internal static void Warn(string text) =>
        Show(text, AppIdentity.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>A Yes/No question. Returns <see langword="true"/> only when the user chose Yes.</summary>
    /// <remarks>Returns a bool rather than a <see cref="DialogResult"/> so a caller cannot accidentally
    /// treat Cancel or a closed dialog as consent - the confirmation convention for this app is that
    /// anything other than an explicit Yes means no.</remarks>
    internal static bool Confirm(string text) =>
        Show(text, AppIdentity.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    /// <summary>A Yes/No confirmation for an action that destroys data. Returns <see langword="true"/>
    /// only when the user chose Yes.</summary>
    /// <remarks>Separate from <see cref="Confirm"/> because the app distinguishes the two: a Question
    /// icon asks something reversible, a Warning icon precedes irreversible loss and its text says so
    /// ("This cannot be undone"). Having both as named methods is what keeps that distinction from
    /// being decided icon-by-icon at each call site. Callers whose icon varies at runtime - the media
    /// import, where only Move destroys anything - still use <see cref="Show"/> directly.</remarks>
    internal static bool ConfirmDestructive(string text) =>
        Show(text, AppIdentity.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

    /// <summary>Shows a themed modal message dialog and returns the <see cref="DialogResult"/> of the
    /// button the user clicked. Mirrors <see cref="MessageBox.Show(string, string, MessageBoxButtons,
    /// MessageBoxIcon)"/>.</summary>
    public static DialogResult Show(string text, string caption = "",
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        using var form = new ThemedMessageBoxForm(text, caption, buttons, icon);
        var owner = Form.ActiveForm;
        if (owner is not null && !owner.IsDisposed && owner.Visible && owner != form)
            return form.ShowDialog(owner); // CenterParent (set in the form) centres over the owner window

        // No visible active owner (e.g. invoked from the tray menu, where the host form is hidden):
        // CenterParent would fall back to the OS default location (top-left), so centre on the screen
        // instead - matching how the native MessageBox behaved for these tray-triggered dialogs.
        form.StartPosition = FormStartPosition.CenterScreen;
        return form.ShowDialog();
    }

    private sealed class ThemedMessageBoxForm : Form
    {
        private const int IconSize = 32;
        private const int IconTextGap = 12;
        private const int MessageMaxWidth = 360;

        private readonly SystemSound? _sound;
        private readonly string _messageText;
        private string[] _buttonLabels = [];
        private Bitmap? _iconBitmap;

        internal ThemedMessageBoxForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            Text = caption;
            _messageText = text;
            DialogLayout.ApplyDialogChrome(this);
            // CenterParent here; the ownerless path overrides it to CenterScreen after construction.
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            _sound = SoundFor(icon);
            BuildLayout(text, icon, buttons);
        }

        private void BuildLayout(string text, MessageBoxIcon icon, MessageBoxButtons buttons)
        {
            var root = DialogLayout.ContentRoot(); // row 0: content, row 1: buttons
            root.Controls.Add(BuildContent(text, icon), 0, 0);

            var specs = ButtonSpecs(buttons);
            _buttonLabels = Array.ConvertAll(specs, s => s.Text); // captured for the Ctrl+C copy
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

        // Ctrl+C copies caption + message + button labels to the clipboard, matching the native
        // MessageBox affordance people use to paste an error into a support thread.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.C))
            {
                const string sep = "---------------------------";
                UiHelpers.SetClipboardTextSafely(string.Join(Environment.NewLine,
                    sep, Text, sep, _messageText, sep, string.Join("   ", _buttonLabels), sep));
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
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
