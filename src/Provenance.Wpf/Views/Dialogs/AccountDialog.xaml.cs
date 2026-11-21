using System.Windows;
using System.Windows.Controls;
using Provenance.Common.Login;
using Provenance.Wpf.Controls;
using Provenance.Wpf.ViewModels.Main;
using Provenance.Wpf.ViewModels.Dialogs;
using Serilog;

namespace Provenance.Wpf.Views.Dialogs;

public partial class AccountDialog : AppWindow
{
    public AccountDialogResult? Result { get; private set; }

    internal AccountFormViewModel Form { get; }

    private readonly AccountChecker? _check;
    private CancellationTokenSource? _checkCts;

    internal AccountDialog(string? accountName, bool useOtp, bool edit, string otpSource, string otpItem, bool requireAll,
                          string polId, string squareEnixId, string otpAccount, AccountChecker? check = null)
    {
        _check = check;
        InitializeComponent();
        Form = new AccountFormViewModel(accountName, useOtp, edit, otpSource, otpItem, requireAll, polId, squareEnixId, otpAccount);
        DataContext = Form;
        Closed += (_, _) =>
        {
            PolPasswordBox.Clear();
            SquareEnixPasswordBox.Clear();
            Form.Close();
        };
        PolPasswordBox.PasswordChanged += (_, _) => Form.PolPasswordTyped = PolPasswordBox.SecurePassword.Length > 0;
        SquareEnixPasswordBox.PasswordChanged += (_, _) => Form.SquareEnixPasswordTyped = SquareEnixPasswordBox.SecurePassword.Length > 0;
        Form.FocusRequested += field => FieldBox(field).Focus();
        Form.BitwardenUrlFocusRequested += () => BwServerUrlBox.Focus();
        Loaded += (_, _) => (polId.Length == 0 ? (Control)PolIdBox : PolPasswordBox).Focus();
        Closing += (_, e) =>
        {
            if (!Form.Checking) return;
            e.Cancel = true;
            CancelCheck();
        };
        Form.Start();
    }

    public static AccountDialogResult? Show(Window? owner, string? accountName, bool useOtp, bool edit,
                                            string otpSource = "", string otpItem = "", bool requireAll = false,
                                            string polId = "", string squareEnixId = "", string otpAccount = "", AccountChecker? check = null)
    {
        var dlg = new AccountDialog(accountName, useOtp, edit, otpSource, otpItem, requireAll, polId, squareEnixId, otpAccount, check);
        return dlg.ShowDialogOver(owner) == true ? dlg.Result : null;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (Form.Checking) return;
        var outcome = Form.TrySave(PolPasswordBox.Password, SquareEnixPasswordBox.Password);
        if (outcome.Result is not { } result) return;
        if (_check is not null && !await Passes(result)) return;
        Result = result;
        PolPasswordBox.Clear();
        SquareEnixPasswordBox.Clear();
        DialogResult = true;
    }

    private async Task<bool> Passes(AccountDialogResult typed)
    {
        using var cts = _checkCts = new CancellationTokenSource();
        Form.ErrorText = "";
        Form.CheckText = AccountCheckRules.VerifyingText;
        Form.Checking = true;
        AccountCheckAnswer answer;
        try { answer = await _check!(typed, cts.Token); }
        catch (OperationCanceledException) { answer = new(false); }
        catch (Exception ex)
        {
            Log.Error(ex, "Checking the account details failed");
            answer = new(false, "Provenance couldn't check these details. Try again.");
        }
        _checkCts = null;
        Form.Checking = false;
        if (answer.Problem is { } problem)
        {
            Form.ErrorText = problem;
            PolPasswordBox.Focus();
        }
        return answer.Finish;
    }

    private void CancelCheck()
    {
        Form.CheckText = "Cancelling...";
        _checkCts?.Cancel();
    }

    private Control FieldBox(AccountField field) => field switch
    {
        AccountField.PolId => PolIdBox,
        AccountField.PolPassword => PolPasswordBox,
        AccountField.SquareEnixId => SquareEnixIdBox,
        AccountField.SquareEnixPassword => SquareEnixPasswordBox,
        _ => OtpItemBox,
    };

    private void OnToolButton(object sender, RoutedEventArgs e) => _ = Form.PressToolButton();

    private void OnBitwardenLogin(object sender, RoutedEventArgs e) => _ = Form.LogInToBitwardenAsync();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (Form.Checking)
        {
            CancelCheck();
            return;
        }
        PolPasswordBox.Clear();
        SquareEnixPasswordBox.Clear();
        DialogResult = false;
    }
}
