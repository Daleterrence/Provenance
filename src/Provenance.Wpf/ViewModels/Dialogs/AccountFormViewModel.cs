using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Login;
using Provenance.Common.Otp;
using Serilog;

namespace Provenance.Wpf.ViewModels.Dialogs;

public sealed record AccountFormOutcome(AccountDialogResult? Result, FormProblem? Problem);

public sealed record AccountChoice(string Id, string Label);

public sealed partial class AccountFormViewModel : ObservableObject
{
    private static readonly string[] Sources = ["", OtpSources.Bitwarden, OtpSources.OnePassword, OtpSources.KeePassXC];

    private readonly bool _required;
    private readonly string _savedAccount;
    private IReadOnlyList<OnePasswordAccount>? _accounts;
    private readonly LatestOnly _toolWork = new();
    private string? _tool;
    private ToolView _view;

    internal Func<string, ToolFind> FindTool { get; set; } = ToolFinder.Find;
    internal Func<string, CancellationToken, Task<IReadOnlyList<OnePasswordAccount>?>> ListAccounts { get; set; } = OnePassword.AccountsAsync;
    internal Func<string, IProgress<double>?, CancellationToken, Task<ToolInstall>> Install { get; set; } = ToolInstaller.InstallAsync;
    internal Func<string?> OpenSettings { get; set; } = ToolFix.OpenSettings;

    internal Func<Task<BitwardenLogin>?> LoginRunning { get; set; } = () => Bitwarden.LoginRunning;
    internal Func<string, Task<BitwardenLogin>> LogIn { get; set; } = Bitwarden.LogInAsync;

    internal ToolView View => _view;

    public event Action<AccountField>? FocusRequested;

    public event Action? BitwardenUrlFocusRequested;

    public string Header { get; }
    public bool AgainVisible { get; }
    public bool DetailsSaved { get; }
    public string PolIdHint => Hint("PlayOnline ID", PolId.Length > 0);
    public string PolPasswordHint => Hint("PlayOnline password", PolPasswordTyped);
    public string SquareEnixIdHint => Hint("Square Enix ID", SquareEnixId.Length > 0);
    public string SquareEnixPasswordHint => Hint("Square Enix password", SquareEnixPasswordTyped);
    public ObservableCollection<AccountChoice> Accounts { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PolIdHint))] private string _polId;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SquareEnixIdHint))] private string _squareEnixId;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PolPasswordHint))] private bool _polPasswordTyped;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SquareEnixPasswordHint))] private bool _squareEnixPasswordTyped;
    [ObservableProperty] private bool _useOtp;
    [ObservableProperty] private string _otpSource;
    [ObservableProperty] private string _otpItem;
    [ObservableProperty] private string _otpItemHint = "";
    [ObservableProperty] private string _otpSourceHelp = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ErrorVisible))] private string _errorText = "";
    [ObservableProperty] private bool _toolPanelVisible;
    [ObservableProperty] private bool _toolSpinnerVisible;
    [ObservableProperty] private bool _toolSpinnerIndeterminate = true;
    [ObservableProperty] private double _toolProgress;
    [ObservableProperty] private string _toolText = "";
    [ObservableProperty] private string? _toolButtonLabel;
    [ObservableProperty] private bool _toolButtonVisible;
    [ObservableProperty] private bool _loginPanelVisible;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BwUrlVisible))] private string _bwServer = "bitwarden.com";
    [ObservableProperty] private string _bwServerUrl = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LoginEnabled))] private bool _loginWaiting;
    [ObservableProperty] private string _loginText = Bitwarden.LoginText;
    [ObservableProperty] private bool _accountPickVisible;
    [ObservableProperty] private AccountChoice? _selectedAccount;

    public bool ItemVisible => OtpSource.Length > 0;

    public bool BwUrlVisible => BwServer is "";

    public bool LoginEnabled => !LoginWaiting;

    public bool ErrorVisible => ErrorText.Length > 0;

    internal AccountFormViewModel(string? accountName, bool useOtp, bool edit, string otpSource, string otpItem, bool requireAll,
                                  string polId, string squareEnixId, string otpAccount)
    {
        _required = !edit || requireAll;
        _savedAccount = otpAccount;
        Header = !edit ? "Add account"
               : string.IsNullOrWhiteSpace(accountName) || accountName.StartsWith("New account", StringComparison.Ordinal)
                   ? "Edit account" : $"Edit {accountName}";
        AgainVisible = requireAll;
        DetailsSaved = edit && !requireAll;
        _useOtp = useOtp;
        _otpSource = Sources.Contains(otpSource) ? otpSource : "";
        _otpItem = otpItem ?? "";
        _polId = polId;
        _squareEnixId = squareEnixId;
        (_otpItemHint, _otpSourceHelp) = OtpSources.Describe(_otpSource);
        _tool = ToolFacts.ForSource(_otpSource);
    }

    private string Hint(string detail, bool typed) => DetailsSaved && !typed ? detail + " (saved)" : detail;

    internal void Start() => _ = LookForToolAsync();

    public void Close() => _toolWork.Cancel();

    partial void OnOtpSourceChanged(string value)
    {
        OnPropertyChanged(nameof(ItemVisible));
        (OtpItemHint, OtpSourceHelp) = OtpSources.Describe(value);
        _ = LookForToolAsync();
    }

    public AccountFormOutcome TrySave(string polPassword, string squareEnixPassword)
    {
        var polId = PolId.Trim().ToUpperInvariant();
        var squareEnixId = SquareEnixId.Trim();
        if (AccountRules.CheckForm(_required, polId, polPassword, squareEnixId, squareEnixPassword, UseOtp, OtpSource, OtpItem) is { } problem)
        {
            ErrorText = problem.Text;
            FocusRequested?.Invoke(problem.Field);
            return new(null, problem);
        }
        return new(new AccountDialogResult(polId, polPassword, squareEnixId, squareEnixPassword, UseOtp, OtpSource,
                                           OtpSource.Length > 0 ? OtpItem.Trim() : "", ChosenAccount()), null);
    }

    internal string ChosenAccount() =>
        OtpSource != OtpSources.OnePassword ? ""
        : _accounts is null ? _savedAccount
        : _accounts.Count > 1 && SelectedAccount is { } picked ? picked.Id
        : "";

    internal async Task LookForToolAsync()
    {
        _toolWork.Cancel();
        _accounts = null;
        AccountPickVisible = false;
        _tool = ToolFacts.ForSource(OtpSource);
        ShowTool(ToolView.Hidden);
        if (_tool is not { } tool) return;
        var mine = _toolWork.Begin();
        ToolFind found;
        try { found = await Task.Run(() => FindTool(tool)); }
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
            try { list = await ListAccounts(path, mine); }
            catch (OperationCanceledException) { return; }
            if (mine.IsCancellationRequested) return;
            ShowAccounts(list);
        }
    }

    internal void ShowAccounts(IReadOnlyList<OnePasswordAccount>? list)
    {
        _accounts = list;
        Accounts.Clear();
        if (list is not { Count: > 1 })
        {
            AccountPickVisible = false;
            return;
        }
        foreach (var a in list) Accounts.Add(new AccountChoice(a.Id, a.Email.Length > 0 ? $"{a.Url} ({a.Email})" : a.Url));
        SelectedAccount = Accounts.FirstOrDefault(i => i.Id == _savedAccount) ?? Accounts[0];
        AccountPickVisible = true;
    }

    internal void ShowTool(ToolView view)
    {
        _view = view;
        var tool = _tool ?? "";
        ToolPanelVisible = view != ToolView.Hidden;
        ToolSpinnerVisible = view == ToolView.Installing;
        ToolSpinnerIndeterminate = true;
        ToolText = ToolFix.Text(view, tool);
        ToolButtonLabel = ToolFix.Button(view, tool) is { } fix ? ToolFix.Label(fix) : null;
        ToolButtonVisible = ToolButtonLabel is not null;
        LoginPanelVisible = view == ToolView.Ready && tool == ToolFacts.Bitwarden;
        if (!LoginPanelVisible) return;
        if (LoginRunning() is { } running) _ = WatchLoginAsync(running);
        else ShowLoginWaiting(false);
    }

    private void ShowLoginWaiting(bool waiting)
    {
        LoginWaiting = waiting;
        LoginText = Bitwarden.LoginText;
    }

    public async Task LogInToBitwardenAsync()
    {
        if (Bitwarden.Server(BwServer ?? "", BwServerUrl) is not { } server)
        {
            LoginText = "Enter the server's address, starting with https://";
            BitwardenUrlFocusRequested?.Invoke();
            return;
        }
        await WatchLoginAsync(LogIn(server));
    }

    private async Task WatchLoginAsync(Task<BitwardenLogin> login)
    {
        var mine = _toolWork.Begin();
        ShowLoginWaiting(true);
        BitwardenLogin r;
        try { r = await login.WaitAsync(mine); }
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
        LoginText = r switch
        {
            BitwardenLogin.LoggedIn => "Logged in to Bitwarden.",
            BitwardenLogin.AlreadyLoggedIn => "Bitwarden is already logged in.",
            BitwardenLogin.ServerRefused => "Bitwarden didn't accept that server address.",
            BitwardenLogin.NoAnswer => "Bitwarden didn't answer.",
            _ => "Bitwarden didn't log in.",
        };
    }

    public Task PressToolButton()
    {
        if (_view != ToolView.Ready) return InstallAsync();
        if (OpenSettings() is { } failed) ToolText = failed;
        return Task.CompletedTask;
    }

    private async Task InstallAsync()
    {
        if (_tool is not { } tool) return;
        var mine = _toolWork.Begin();
        ShowTool(ToolView.Installing);
        var progress = new Progress<double>(f =>
        {
            if (mine.IsCancellationRequested || _view != ToolView.Installing) return;
            ToolSpinnerIndeterminate = false;
            ToolProgress = f * 100;
        });
        ToolInstall r;
        try { r = await Install(tool, progress, mine); }
        catch (OperationCanceledException) { return; }
        if (mine.IsCancellationRequested) return;
        if (r == ToolInstall.Installed) await LookForToolAsync();
        else ShowTool(ToolFix.ViewFor(r));
    }
}
