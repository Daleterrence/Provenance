using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Provenance.Common.Login;
using Provenance.Common.Otp;
using Provenance.Wpf.Controls;
using Serilog;

namespace Provenance.Wpf.Views.Dialogs;

// The only place the IDs and passwords get typed, they stay off screen everywhere else
public partial class AccountDialog : AppWindow
{
    private readonly bool _required;   // adding, or the saved details can't be used any more
    private readonly string _savedAccount;
    private IReadOnlyList<OnePasswordAccount>? _accounts;   // null until op's listed them, or when it couldn't
    private CancellationTokenSource? _toolWork;
    private string? _tool;
    private ToolView _view;
    public AccountDialogResult? Result { get; private set; }

    internal AccountDialog(string? accountName, bool useOtp, bool edit, string otpSource, string otpItem, bool requireAll,
                          string polId, string squareEnixId, string otpAccount)
    {
        InitializeComponent();
        _required = !edit || requireAll;
        _savedAccount = otpAccount;
        BwLoginText.Text = Bitwarden.LoginText;
        Closed += (_, _) => _toolWork?.Cancel();
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
        SquareEnixIdBox.Text = squareEnixId;
        KeepHint.Visibility = edit && !requireAll ? Visibility.Visible : Visibility.Collapsed;
        AgainText.Visibility = requireAll ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => (polId.Length == 0 ? (Control)PolIdBox : PolPasswordBox).Focus();
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
        var polId = PolIdBox.Text.Trim().ToUpperInvariant();
        var squareEnixId = SquareEnixIdBox.Text.Trim();
        var polPw = PolPasswordBox.Password;
        var squareEnixPassword = SquareEnixPasswordBox.Password;

        (string Text, Control Field)? problem = null;
        if ((_required || polId.Length > 0) && (polId.Length != 8 || !polId.All(char.IsAsciiLetterOrDigit)))
            problem = ("A PlayOnline ID is 8 letters and numbers.", PolIdBox);
        else if (_required && polPw.Length == 0) problem = ("Enter your PlayOnline password.", PolPasswordBox);
        else if (polPw.Length > HookExitText.MaxPolPasswordLength)
            problem = ("A PlayOnline password can't be longer than 16 characters.", PolPasswordBox);
        else if (AccountRules.PasswordProblem(polPw) is { } polPwProblem) problem = (polPwProblem, PolPasswordBox);
        else if (_required && squareEnixId.Length == 0) problem = ("Enter your Square Enix ID.", SquareEnixIdBox);
        else if (squareEnixId.Contains('@')) problem = ("Use your Square Enix ID here, not your email address.", SquareEnixIdBox);
        else if (AccountRules.SquareEnixIdProblem(squareEnixId) is { } squareEnixIdProblem) problem = (squareEnixIdProblem, SquareEnixIdBox);
        else if (_required && squareEnixPassword.Length == 0) problem = ("Enter your Square Enix password.", SquareEnixPasswordBox);
        else if (AccountRules.PasswordProblem(squareEnixPassword) is { } squareEnixPasswordProblem) problem = (squareEnixPasswordProblem, SquareEnixPasswordBox);
        else if (OtpBox.IsChecked == true && OtpSource.Length > 0 && OtpItemBox.Text.Trim().Length == 0)
            problem = ("Enter the item that holds the authenticator, or choose to type the code in.", OtpItemBox);
        else if (OtpBox.IsChecked == true && ToolFacts.ForSource(OtpSource) is not null && OtpSources.StartsWithDash(OtpItemBox.Text))
            problem = (OtpSources.DashText, OtpItemBox);

        if (problem is { } p)
        {
            ErrorText.Text = p.Text;
            ErrorText.Visibility = Visibility.Visible;
            p.Field.Focus();
            return;
        }

        Result = new AccountDialogResult(polId, polPw, squareEnixId, squareEnixPassword, OtpBox.IsChecked == true,
                                         OtpSource, OtpSource.Length > 0 ? OtpItemBox.Text.Trim() : "", ChosenAccount());
        PolPasswordBox.Clear();
        SquareEnixPasswordBox.Clear();
        DialogResult = true;
    }

    private string OtpSource => (OtpSourceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void OnOtpSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OtpItemBox is null) return;   // still inside InitializeComponent
        var (hint, help) = OtpSources.Describe(OtpSource);
        OtpItemBox.Visibility = OtpSourceHelp.Visibility = OtpSource.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        MaterialDesignThemes.Wpf.HintAssist.SetHint(OtpItemBox, hint);
        OtpSourceHelp.Text = help;
        _ = LookForToolAsync();
    }

    // with one account op's choice is the only one, and when op couldn't list them the saved choice stays
    internal string ChosenAccount() =>
        OtpSource != OtpSources.OnePassword ? ""
        : _accounts is null ? _savedAccount
        : _accounts.Count > 1 && OpAccountBox.SelectedItem is ComboBoxItem { Tag: string id } ? id
        : "";

    // a copy found here gets checked again before each use, this is only so the dialog can offer to install one
    private async Task LookForToolAsync()
    {
        _toolWork?.Cancel();
        _accounts = null;
        OpAccountPanel.Visibility = Visibility.Collapsed;
        _tool = ToolFacts.ForSource(OtpSource);
        ShowTool(ToolView.Hidden);
        if (_tool is not { } tool) return;
        var mine = _toolWork = new CancellationTokenSource();
        ToolFind found;
        try { found = await Task.Run(() => ToolFinder.Find(tool)); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Looking for {Name}'s command-line tool failed", ToolFacts.Name(tool));
            return;
        }
        using (found)
        {
            if (mine.IsCancellationRequested) return;
            if (found.Path is not { } path)
            {
                ShowTool(ToolFix.ViewFor(found.State));
                return;
            }
            ShowTool(ToolView.Ready);
            if (tool != ToolFacts.OnePassword) return;
            IReadOnlyList<OnePasswordAccount>? list;
            try { list = await OnePassword.AccountsAsync(path, mine.Token); }
            catch (OperationCanceledException) { return; }
            if (mine.IsCancellationRequested) return;
            ShowAccounts(list);
        }
    }

    internal void ShowAccounts(IReadOnlyList<OnePasswordAccount>? list)
    {
        _accounts = list;
        OpAccountBox.Items.Clear();
        if (list is not { Count: > 1 }) return;
        foreach (var a in list)
            OpAccountBox.Items.Add(new ComboBoxItem { Content = a.Email.Length > 0 ? $"{a.Url} ({a.Email})" : a.Url, Tag = a.Id });
        var items = OpAccountBox.Items.OfType<ComboBoxItem>().ToList();
        OpAccountBox.SelectedItem = items.FirstOrDefault(i => (string)i.Tag == _savedAccount) ?? items[0];
        OpAccountPanel.Visibility = Visibility.Visible;
    }

    internal void ShowTool(ToolView view)
    {
        _view = view;
        var tool = _tool ?? "";
        ToolPanel.Visibility = view == ToolView.Hidden ? Visibility.Collapsed : Visibility.Visible;
        ToolSpinner.Visibility = view == ToolView.Installing ? Visibility.Visible : Visibility.Collapsed;
        ToolSpinner.IsIndeterminate = true;
        ToolText.Text = ToolFix.Text(view, tool);
        ToolButton.Content = ToolFix.Button(view, tool) is { } fix ? ToolFix.Label(fix) : null;
        ToolButton.Visibility = ToolButton.Content is null ? Visibility.Collapsed : Visibility.Visible;
        bool login = view == ToolView.Ready && tool == ToolFacts.Bitwarden;
        BitwardenLoginPanel.Visibility = login ? Visibility.Visible : Visibility.Collapsed;
        if (!login) return;
        // a login window that's still open gets watched again, and nothing's left greyed out once there isn't one
        if (Bitwarden.LoginRunning is { } running) _ = WatchLoginAsync(running);
        else ShowLoginWaiting(false);
    }

    private void ShowLoginWaiting(bool waiting)
    {
        BwLoginButton.IsEnabled = BwServerBox.IsEnabled = BwServerUrlBox.IsEnabled = !waiting;
        BwLoginSpinner.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        BwLoginText.Text = Bitwarden.LoginText;
    }

    private void OnToolButton(object sender, RoutedEventArgs e)
    {
        if (_view != ToolView.Ready) _ = InstallAsync();
        else if (ToolFix.OpenSettings() is { } failed) ToolText.Text = failed;
    }

    private async Task InstallAsync()
    {
        if (_tool is not { } tool) return;
        _toolWork?.Cancel();
        var mine = _toolWork = new CancellationTokenSource();
        ShowTool(ToolView.Installing);
        var progress = new Progress<double>(f =>
        {
            if (mine.IsCancellationRequested || _view != ToolView.Installing) return;
            ToolSpinner.IsIndeterminate = false;
            ToolSpinner.Value = f * 100;
        });
        ToolInstall r;
        try { r = await ToolInstaller.InstallAsync(tool, progress, mine.Token); }
        catch (OperationCanceledException) { return; }
        if (mine.IsCancellationRequested) return;
        if (r == ToolInstall.Installed) await LookForToolAsync();
        else ShowTool(ToolFix.ViewFor(r));
    }

    private void OnBwServerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BwServerUrlBox is null) return;   // still inside InitializeComponent
        BwServerUrlBox.Visibility = (BwServerBox.SelectedItem as ComboBoxItem)?.Tag is "" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBitwardenLogin(object sender, RoutedEventArgs e) => _ = LogInToBitwardenAsync();

    private async Task LogInToBitwardenAsync()
    {
        var picked = (BwServerBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        if (Bitwarden.Server(picked, BwServerUrlBox.Text) is not { } server)
        {
            BwLoginText.Text = "Enter the server's address, starting with https://";
            BwServerUrlBox.Focus();
            return;
        }
        await WatchLoginAsync(Bitwarden.LogInAsync(server));
    }

    // only stops watching when the dialog closes or the source changes, the login itself carries on and locks after
    private async Task WatchLoginAsync(Task<BitwardenLogin> login)
    {
        _toolWork?.Cancel();
        var mine = _toolWork = new CancellationTokenSource();
        ShowLoginWaiting(true);
        BitwardenLogin r;
        try { r = await login.WaitAsync(mine.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Logging in to Bitwarden failed");
            r = BitwardenLogin.NotLoggedIn;
        }
        if (mine.IsCancellationRequested) return;
        ShowLoginWaiting(false);
        if (r == BitwardenLogin.NoTool)
        {
            await LookForToolAsync();
            return;
        }
        BwLoginText.Text = r switch
        {
            BitwardenLogin.LoggedIn => "Logged in to Bitwarden.",
            BitwardenLogin.AlreadyLoggedIn => "Bitwarden is already logged in.",
            BitwardenLogin.ServerRefused => "Bitwarden didn't accept that server address.",
            BitwardenLogin.NoAnswer => "Bitwarden didn't answer.",
            _ => "Bitwarden didn't log in.",
        };
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        PolPasswordBox.Clear();
        SquareEnixPasswordBox.Clear();
        DialogResult = false;
    }
}
