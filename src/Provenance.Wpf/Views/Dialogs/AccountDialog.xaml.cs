using System.Windows;
using System.Windows.Controls;
using Provenance.Common.Login;
using Provenance.Wpf.Controls;
using Provenance.Wpf.ViewModels.Dialogs;

namespace Provenance.Wpf.Views.Dialogs;

// The only place the IDs and passwords get typed
public partial class AccountDialog : AppWindow
{
    public AccountDialogResult? Result { get; private set; }

    internal AccountFormViewModel Form { get; }

    internal AccountDialog(string? accountName, bool useOtp, bool edit, string otpSource, string otpItem, bool requireAll,
                          string polId, string squareEnixId, string otpAccount)
    {
        InitializeComponent();
        Form = new AccountFormViewModel(accountName, useOtp, edit, otpSource, otpItem, requireAll, polId, squareEnixId, otpAccount);
        DataContext = Form;
        Closed += (_, _) =>
        {
            PolPasswordBox.Clear();
            SquareEnixPasswordBox.Clear();
            Form.Close();
        };
        Form.FocusRequested += field => FieldBox(field).Focus();
        Form.BitwardenUrlFocusRequested += () => BwServerUrlBox.Focus();
        Loaded += (_, _) => (polId.Length == 0 ? (Control)PolIdBox : PolPasswordBox).Focus();
        Form.Start();
    }

    public static AccountDialogResult? Show(Window? owner, string? accountName, bool useOtp, bool edit,
                                            string otpSource = "", string otpItem = "", bool requireAll = false,
                                            string polId = "", string squareEnixId = "", string otpAccount = "")
    {
        var dlg = new AccountDialog(accountName, useOtp, edit, otpSource, otpItem, requireAll, polId, squareEnixId, otpAccount);
        return dlg.ShowDialogOver(owner) == true ? dlg.Result : null;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var outcome = Form.TrySave(PolPasswordBox.Password, SquareEnixPasswordBox.Password);
        if (outcome.Result is not { } result) return;
        Result = result;
        PolPasswordBox.Clear();
        SquareEnixPasswordBox.Clear();
        DialogResult = true;
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
        PolPasswordBox.Clear();
        SquareEnixPasswordBox.Clear();
        DialogResult = false;
    }
}
