using System.Windows;
using System.Windows.Input;
using Provenance.Wpf.Controls;

namespace Provenance.Wpf.Views.Dialogs;

// WPF doesn't ship an InputBox, so here's a tiny one
public partial class InputDialog : AppWindow
{
    private Func<string, string?> _check = _ => null;
    private bool _secret;

    private string Value => _secret ? SecretBox.Password : InputBox.Text.Trim();

    internal InputDialog()
    {
        InitializeComponent();
    }

    // the trimmed text, or null when cancelled. validate returns the problem shown under the box, null when it's fine
    public static string? Prompt(Window owner, string title, string prompt, string initialValue = "",
                                 int maxLength = 64, bool allowEmpty = false, Func<string, string?>? validate = null)
    {
        var dlg = new InputDialog { Title = title };
        dlg.PromptText.Text = prompt;
        dlg.InputBox.MaxLength = maxLength;
        dlg.InputBox.Text = initialValue;
        dlg._check = t => string.IsNullOrEmpty(t) && !allowEmpty ? "Type something first." : validate?.Invoke(t);
        dlg.InputBox.TextChanged += (_, _) => dlg.ErrorText.Visibility = Visibility.Collapsed;
        dlg.Loaded += (_, _) =>
        {
            dlg.InputBox.Focus();
            dlg.InputBox.SelectAll();
        };
        return dlg.ShowDialogOver(owner) == true ? dlg.InputBox.Text.Trim() : null;
    }

    // the password as typed (never trimmed), or null when cancelled
    public static string? PromptSecret(Window owner, string title, string prompt, int maxLength = 64,
                                       Func<string, string?>? validate = null)
    {
        var dlg = ForSecret(title, prompt, maxLength, validate);
        try { return dlg.ShowDialogOver(owner) == true ? dlg.SecretBox.Password : null; }
        finally { dlg.SecretBox.Clear(); }
    }

    // set up but not shown yet
    internal static InputDialog ForSecret(string title, string prompt, int maxLength, Func<string, string?>? validate)
    {
        var dlg = new InputDialog { Title = title, _secret = true };
        dlg.PromptText.Text = prompt;
        dlg.InputBox.Visibility = Visibility.Collapsed;
        dlg.SecretBox.Visibility = Visibility.Visible;
        dlg.SecretBox.MaxLength = maxLength;
        MaterialDesignThemes.Wpf.PasswordBoxAssist.SetIsPasswordRevealed(dlg.SecretBox, false);
        dlg._check = t => validate?.Invoke(t);
        dlg.SecretBox.PasswordChanged += (_, _) => dlg.ErrorText.Visibility = Visibility.Collapsed;
        dlg.Loaded += (_, _) => dlg.SecretBox.Focus();
        return dlg;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_check(Value) is { } problem)
        {
            ErrorText.Text = problem;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    // handled here so the Cancel button's own Esc (IsCancel) doesn't close it a second time
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        OnCancel(sender, e);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
