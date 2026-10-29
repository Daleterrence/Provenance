using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Provenance.Wpf.Views;

// In edit mode an empty string means keep the saved value. ToString prints *** for every ID and password
public sealed record AccountDialogResult(string PolId, string PolPassword,
                                         string SeaId, string SeaPassword, bool UseOtp,
                                         string OtpSource = "", string OtpItem = "")
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("PolId = ***, PolPassword = ***, SeaId = ***, SeaPassword = ***, UseOtp = ").Append(UseOtp)
               .Append(", OtpSource = ").Append(OtpSource).Append(", OtpItem = ").Append(OtpItem);
        return true;
    }
}

// The only place the IDs and passwords get typed, they stay off screen everywhere else
public partial class AccountDialog : Window
{
    private readonly bool _required;   // adding, or the saved details can't be used any more
    public AccountDialogResult? Result { get; private set; }

    private AccountDialog(string? accountName, bool useOtp, bool edit, string otpSource, string otpItem, bool requireAll,
                          string polId, string seaId)
    {
        InitializeComponent();
        WindowFrame.Attach(this);
        _required = !edit || requireAll;
        // before its first sign-in the name's a placeholder ("New account"), which reads badly after "Edit"
        HeaderText.Text = !edit ? "Add account"
                        : string.IsNullOrWhiteSpace(accountName) || accountName.StartsWith("New account", StringComparison.Ordinal)
                            ? "Edit account" : $"Edit {accountName}";
        OtpBox.IsChecked = useOtp;
        OtpSourceBox.SelectedItem = OtpSourceBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == otpSource) ?? OtpSourceBox.Items[0];
        OtpItemBox.Text = otpItem;
        // IDs typed here the last time it was open, never a saved value and never a password
        PolIdBox.Text = polId;
        SeaIdBox.Text = seaId;
        KeepHint.Visibility = edit && !requireAll ? Visibility.Visible : Visibility.Collapsed;
        AgainText.Visibility = requireAll ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => (polId.Length == 0 ? (Control)PolIdBox : PolPasswordBox).Focus();
    }

    public static AccountDialogResult? Show(Window? owner, string? accountName, bool useOtp, bool edit,
                                            string otpSource = "", string otpItem = "", bool requireAll = false,
                                            string polId = "", string seaId = "")
    {
        var dlg = new AccountDialog(accountName, useOtp, edit, otpSource, otpItem, requireAll, polId, seaId);
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var polId = PolIdBox.Text.Trim().ToUpperInvariant();
        var seaId = SeaIdBox.Text.Trim();
        var polPw = PolPasswordBox.Password;
        var seaPw = SeaPasswordBox.Password;

        (string Text, Control Field)? problem = null;
        if ((_required || polId.Length > 0) && (polId.Length != 8 || !polId.All(char.IsAsciiLetterOrDigit)))
            problem = ("A PlayOnline ID is 8 letters and numbers.", PolIdBox);
        else if (_required && polPw.Length == 0) problem = ("Enter your PlayOnline password.", PolPasswordBox);
        else if (PasswordProblem(polPw) is { } polPwProblem) problem = (polPwProblem, PolPasswordBox);
        else if (_required && seaId.Length == 0) problem = ("Enter your Square Enix ID.", SeaIdBox);
        else if (seaId.Contains('@')) problem = ("Use your Square Enix ID here, not your email address.", SeaIdBox);
        else if (SeaIdProblem(seaId) is { } seaIdProblem) problem = (seaIdProblem, SeaIdBox);
        else if (_required && seaPw.Length == 0) problem = ("Enter your Square Enix password.", SeaPasswordBox);
        else if (PasswordProblem(seaPw) is { } seaPwProblem) problem = (seaPwProblem, SeaPasswordBox);
        else if (OtpBox.IsChecked == true && OtpSource.Length > 0 && OtpItemBox.Text.Trim().Length == 0)
            problem = ("Enter the item that holds the authenticator, or choose to type the code in.", OtpItemBox);

        if (problem is { } p)
        {
            ErrorText.Text = p.Text;
            ErrorText.Visibility = Visibility.Visible;
            p.Field.Focus();
            return;
        }

        Result = new AccountDialogResult(polId, polPw, seaId, seaPw, OtpBox.IsChecked == true,
                                         OtpSource, OtpSource.Length > 0 ? OtpItemBox.Text.Trim() : "");
        PolPasswordBox.Clear();
        SeaPasswordBox.Clear();
        DialogResult = true;
    }

    // the hook hands these to POL in the ANSI code page, so anything outside printable ASCII arrives changed, gets
    // refused, and the refusal counts toward the lockout
    public static string? PasswordProblem(string password) =>
        IsPrintableAscii(password) ? null : "Passwords can only use English letters, numbers and keyboard symbols.";

    public static string? SeaIdProblem(string seaId) =>
        IsPrintableAscii(seaId) ? null : "A Square Enix ID can only use English letters, numbers and keyboard symbols.";

    private static bool IsPrintableAscii(string s) => s.All(c => c is >= '!' and <= '~');   // 0x21 to 0x7E

    private string OtpSource => (OtpSourceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void OnOtpSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OtpItemBox is null) return;   // still inside InitializeComponent
        var (hint, help) = Services.OtpSources.Describe(OtpSource);
        OtpItemBox.Visibility = OtpSourceHelp.Visibility = OtpSource.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        MaterialDesignThemes.Wpf.HintAssist.SetHint(OtpItemBox, hint);
        OtpSourceHelp.Text = help;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        PolPasswordBox.Clear();
        SeaPasswordBox.Clear();
        DialogResult = false;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBoxBase or PasswordBox) return;
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
