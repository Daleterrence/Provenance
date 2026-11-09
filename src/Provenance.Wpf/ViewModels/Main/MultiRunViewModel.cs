using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common;
using Provenance.Common.Credentials;
using Provenance.Common.Game;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Login;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Progress;
using Provenance.Common.Settings;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Serilog;

namespace Provenance.Wpf.ViewModels.Main;

// what a run needs from the main window's own login and Play code
internal sealed record MultiRunLinks(
    Func<bool> LoggedInOrLaunching,
    Action StopSocialPoll,
    Func<bool> EnsureMemberAgreement,
    Func<string?> PromptHandleName,
    Func<(string Ffxi, string Viewer, PolRegion Region)?> GameFolders,
    Func<PolRegion> PlayRegion,
    Action<string?> WritePolOptions,
    Func<string?, Action<string>?, Task> BackUpGameSettings);

public partial class MultiRunViewModel : ObservableObject, IMultiRunHost
{
    private readonly IDialogs _dialogs;
    private readonly IWindowManager _windows;
    private readonly ISettingsService _settings;
    private readonly ICredentialService _credentials;
    private readonly PolSession _pol;
    private readonly UiThread _ui;
    private readonly AccountsViewModel _accounts;
    private readonly MultiRunLinks _links;

    public event Action? RequestShow;
    public event Action? RequestExit;
    // the accounts list or a run's state changed, for an open Settings page
    public event Action? Changed;

    internal MultiRunViewModel(IDialogs dialogs, IWindowManager windows, ISettingsService settings, ICredentialService credentials,
                               PolSession pol, UiThread ui, AccountsViewModel accounts, MultiRunLinks links)
    {
        _dialogs = dialogs;
        _windows = windows;
        _settings = settings;
        _credentials = credentials;
        _pol = pol;
        _ui = ui;
        _accounts = accounts;
        _links = links;
    }

    // each progress report restarts the clock, so the first account can download an update of any size
    private static readonly TimeSpan NoProgressLimit = TimeSpan.FromMinutes(10);

    private List<MultiAccountEntry> MultiEntries() =>
        MultiAccountRules.RunEntries(_settings.Current.MultiAccounts, _accounts.Profiles.Select(p => p.Id));

    public int AccountCount => MultiEntries().Count;
    public bool HasAccounts => Running || (AccountCount > 0 && MultiPlatformChosen());
    public string StartAllText => Stopping ? "STOPPING..." : Running ? "STOP ALL ACCOUNTS" : $"START ALL ACCOUNTS ({AccountCount})";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartAllText), nameof(HasAccounts))]
    [NotifyCanExecuteChangedFor(nameof(StartAllAccountsCommand))]
    private bool _running;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartAllText))]
    [NotifyCanExecuteChangedFor(nameof(StartAllAccountsCommand))]
    private bool _stopping;

    [ObservableProperty] private string _status = "";

    private CancellationTokenSource? _cts;
    private bool _exitAfterStop;

    private IReadOnlyList<MultiRunAccount> _runAccounts = [];
    private Dictionary<string, string?> _runHookProfiles = new();
    private ThirdPartyHook _runPlatform;

    internal PolSocialSnapshot? RunSnapshot { get; set; }
    private readonly List<string> _lateGameFailures = [];
    private bool _runBackedUp;

    internal void Refresh()
    {
        OnPropertyChanged(nameof(AccountCount));
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(StartAllText));
    }

    private bool MultiPlatformChosen() =>
        _pol.Platform is ThirdPartyHook.Windower or ThirdPartyHook.Ashita;

    private bool CanStartOrStop() => Running ? !Stopping : !_links.LoggedInOrLaunching() && _pol.Pid is null;

    // concurrent, so the same button stays clickable as the stop while a run goes
    [RelayCommand(CanExecute = nameof(CanStartOrStop), AllowConcurrentExecutions = true)]
    private async Task StartAllAccountsAsync()
    {
        if (Running) { StopAllAccounts(); return; }
        if (_links.LoggedInOrLaunching() || _pol.Pid is not null) return;
        if (!MultiPlatformChosen())
        {
            Log.Warning("Multi-account mode: not started, Windower or Ashita is not chosen");
            return;
        }
        if (!_links.EnsureMemberAgreement()) return;
        var entries = MultiEntries();
        if (entries.Count == 0) return;
        var accounts = entries.Select(e => _accounts.Profiles.First(p => p.Id == e.ProfileId))
                              .Select(p => new MultiRunAccount(p.Id, string.IsNullOrWhiteSpace(p.HandleName) ? p.DisplayName : p.HandleName!))
                              .ToList();
        if (MultiAccountRules.WithoutProfile(entries) is { Count: > 0 } unpicked)
        {
            Log.Warning("Multi-account mode: not started, {Count} account(s) have no profile picked", unpicked.Count);
            var names = unpicked.Select(e => accounts.First(a => a.ProfileId == e.ProfileId).Name).ToList();
            _dialogs.Error("Multi-account mode", MultiAccountRules.NoProfileText(_pol.Platform, names));
            return;
        }
        _runAccounts = accounts;
        _runHookProfiles = entries.ToDictionary(e => e.ProfileId, e => e.ThirdPartyHookProfile);
        _runPlatform = _pol.Platform;
        _exitAfterStop = false;
        _runBackedUp = false;
        _lateGameFailures.Clear();
        // a friends window left open mustn't write requests to the run's lean sessions
        _links.StopSocialPoll();
        _windows.CloseSessionWindows();
        Running = true;
        _cts = new CancellationTokenSource();
        var options = new MultiRunOptions(_settings.Current.MultiContinueOnError, _settings.Current.MultiCloseWhenFinished);
        var status = new Progress<string>(SetRunStatus);
        Log.Information("Multi-account mode: starting {Count} account(s)", accounts.Count);
        MultiRunResult? result = null;
        string? unexpected = null;
        try { result = await MultiAccountRunner.RunAsync(accounts, options, this, status, _cts.Token); }
        catch (Exception ex)
        {
            Log.Error(ex, "Multi-account mode: the run ended with an error");
            unexpected = ErrorText.Of(ex, "Provenance stopped starting your accounts because of a problem it didn't expect.");
        }
        finally
        {
            Running = false;
            Stopping = false;
            Status = "";
            _cts.Dispose();
            _cts = null;
            RunSnapshot = null;
            _runAccounts = [];
            _runHookProfiles = new();
            _runPlatform = ThirdPartyHook.None;
            Changed?.Invoke();
        }
        var failures = result?.Failures ?? [];
        Log.Information("Multi-account mode: finished, {Failures} failure(s), stopped {Stopped}", failures.Count, result?.Stopped);
        if (_exitAfterStop) { RequestExit?.Invoke(); return; }
        if (failures.Count > 0 || unexpected != null || _lateGameFailures.Count > 0)
        {
            var lines = failures.Select(f => $"{f.Account.Name}: {f.Failure.Text}").Concat(_lateGameFailures).ToList();
            _lateGameFailures.Clear();
            if (unexpected != null) lines.Add(unexpected);
            // a long run can end with the window in the tray
            RequestShow?.Invoke();
            _dialogs.Error("Multi-account mode", string.Join("\n", lines), offerLogs: true);
            return;
        }
        if (result is { CloseApp: true }) RequestExit?.Invoke();
    }

    // saved straight away so a crash can't lose it, and found by ID as Settings may have swapped the list mid-run
    private void ApplyFailure(MultiRunAccount account, AccountFailure failure)
    {
        Log.Information("Multi-account mode: {Account} failed ({Kind}): {Text}", account.ProfileId, failure.Kind, failure.Text);
        if (failure.Kind is not (AccountFailureKind.Credential or AccountFailureKind.AccountBlocked)) return;
        if (!_settings.Current.MultiAccounts.Any(e => e.ProfileId == account.ProfileId)) return;
        _settings.SaveChange(s =>
        {
            if (s.MultiAccounts.FirstOrDefault(e => e.ProfileId == account.ProfileId) is not { } e) return;
            e.Enabled = false;
            e.Problem = failure.Text;
            if (failure.Kind == AccountFailureKind.Credential) e.CredentialsRefused = true;
        });
        Refresh();
        Changed?.Invoke();
    }

    public void StopAllAccounts()
    {
        if (!Running || Stopping || _cts is null) return;
        Log.Warning("Multi-account mode: stop");
        Stopping = true;
        Status = "Stopping...";
        _cts.Cancel();
    }

    internal bool ConfirmStopAccounts() => _dialogs.Confirm("Multi-account mode",
        "Your accounts are still starting. Stop them and close Provenance?", "STOP AND CLOSE", ConfirmStyle.Destructive);

    public void StopAllAccountsAndExit()
    {
        if (!Running) return;
        _exitAfterStop = true;
        StopAllAccounts();
    }

    private void SetRunStatus(string text)
    {
        if (Running && !Stopping) Status = text;
    }

    bool IMultiRunHost.IsInGame(string profileId) => PolSession.GameRunningFor(profileId);

    async Task<AccountFailure?> IMultiRunHost.LogInAndPlayAsync(MultiRunAccount account, CancellationToken ct)
    {
        try
        {
            return await _ui.RunAsync(() => RunAccountAsync(account, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // the dispatcher refused the work (the app's closing), still a failure and never an escape
            Log.Error(ex, "Multi-account mode: {Account} could not run", account.ProfileId);
            return new AccountFailure(AccountFailureKind.Account, ErrorText.Of(ex, "Provenance couldn't start this account."));
        }
    }

    private async Task<AccountFailure?> RunAccountAsync(MultiRunAccount account, CancellationToken ct)
    {
        AccountFailure? failure;
        try { failure = await RunOneAccountAsync(account, ct); }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            if (ex is not OperationCanceledException) Log.Warning(ex, "Multi-account mode: {Account} ended during the stop", account.ProfileId);
            Log.Information("Multi-account mode: {Account} stopped", account.ProfileId);
            await LetGoOfAccountAsync();
            if (ex is OperationCanceledException) throw;
            throw new OperationCanceledException("The run was stopped.", ex, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Multi-account mode: {Account} failed", account.ProfileId);
            await LetGoOfAccountAsync();
            // no key file means every account would fail the same way, so the run stops (Install)
            return new AccountFailure(ex is CredentialKeyUnavailableException ? AccountFailureKind.Install : AccountFailureKind.Account,
                                      ErrorText.Of(ex, "Provenance ran into a problem with this account."));
        }
        if (failure is null) return null;
        try { ApplyFailure(account, failure); }
        catch (Exception ex) { Log.Error(ex, "Multi-account mode: could not record the failure of {Account}", account.ProfileId); }
        return failure;
    }

    // Null when the game started. Before Play a failure or stop signs the session out, after Play it's only released.
    private async Task<AccountFailure?> RunOneAccountAsync(MultiRunAccount account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int number = Math.Max(1, _runAccounts.ToList().FindIndex(a => a.ProfileId == account.ProfileId) + 1);
        string step = $"({number} of {_runAccounts.Count})";

        if (!MultiPlatformChosen())
            return new(AccountFailureKind.Install, "You must use either Windower or Ashita with Provenance to use multi-account login.");
        // after a switch between Windower and Ashita mid-run a stored profile means nothing
        var hookProfile = _pol.Platform == _runPlatform ? _runHookProfiles.GetValueOrDefault(account.ProfileId) : null;
        if (_pol.Platform == ThirdPartyHook.Ashita && hookProfile is { Length: > 0 } boot
            && !AshitaBootFiles.IsBootFileName(boot))
            return new(AccountFailureKind.Account, AshitaLauncher.AccountBootFileProblem);

        var profile = _accounts.Profiles.FirstOrDefault(p => p.Id == account.ProfileId);
        if (profile is null) return AccountRules.MissingDetailsFailure(default);
        var details = AccountRules.PrepareLogIn(_credentials, profile.Id);
        if (details.Missing) return AccountRules.MissingDetailsFailure(_credentials.ReadIds(profile.Id));
        if (details.Problem is { } problem) return new(AccountFailureKind.Account, problem);
        var (pol, squareEnix) = (details.Pol!, details.SquareEnix!);

        if (!_pol.TryClaimAccount(profile.Id, out var claimProblem))
            return new(AccountFailureKind.Account, claimProblem ?? "This account is in use in another copy of Provenance.");

        // outside the no-progress guard, it waits for the user
        string otp = "";
        if (profile.UseOtp)
        {
            var code = _accounts.AskOtp(profile, squareEnix);
            if (code is null || ct.IsCancellationRequested)
            {
                _pol.ReleaseClaim();
                ct.ThrowIfCancellationRequested();
                return new(AccountFailureKind.Account, "The one-time password was cancelled.");
            }
            otp = code;
        }

        using var guard = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void Kick() { try { guard.CancelAfter(NoProgressLimit); } catch (ObjectDisposedException) { } }
        void Pause() { try { guard.CancelAfter(Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { } }
        Kick();
        var token = guard.Token;
        async Task<AccountFailure?> Stopped()
        {
            await LetGoOfAccountAsync();
            ct.ThrowIfCancellationRequested();
            return new(AccountFailureKind.Account, "This account made no progress for 10 minutes, so Provenance stopped it.");
        }
        async Task<AccountFailure?> LetGo(AccountFailureKind kind, string text)
        {
            await LetGoOfAccountAsync();
            return new(kind, text);
        }

        string? lastFailure = null;
        var progressLog = new ProgressLog();
        var progress = new InlineProgress<LaunchProgress>(p =>
        {
            Kick();
            progressLog.Write(p);
            if (p.Kind == LaunchProgressKind.Failed) lastFailure = p.Detail;
        });
        string? AskHandleName()
        {
            Pause();
            try { return _links.PromptHandleName(); }
            finally { Kick(); }
        }
        RunSnapshot = null;
        int logIn;
        try
        {
            logIn = await _pol.ConnectAsync(pol, squareEnix, otp, profile.Id, progress, AskHandleName, token,
                                             new SessionOptions(Lean: true, ThirdPartyHookProfile: hookProfile,
                                                                HomeSlot: _accounts.EnsureHomeSlot(profile)));
        }
        catch (Exception) when (token.IsCancellationRequested) { return await Stopped(); }
        if (logIn != 0)
        {
            await LetGoOfAccountAsync();
            if (logIn == LoginCodes.Cancelled && token.IsCancellationRequested) return await Stopped();
            var kind = AccountFailures.Classify(logIn, _pol.LastRefusalReason, profile.UseOtp);
            return new(kind, lastFailure ?? $"The login didn't finish (code {logIn}).");
        }
        SetRunStatus($"{account.Name} is logged in. Starting the game {step}...");

        var snap = RunSnapshot ?? (_pol.Pid is { } pid ? SocialSnapshotFiles.TryRead(pid) : null);
        if (AccountRules.PlayGateFailure(snap) is { } gate) return await LetGo(gate.Kind, gate.Text);
        if (token.IsCancellationRequested) return await Stopped();

        if (_links.GameFolders() is { } dirs)
        {
            string eta = "";
            var left = new PhaseTimeLeft(text => eta = text);   // UI thread only, like the status
            var patch = new InlineProgress<PatchProgress>(p =>
            {
                Kick();
                if (string.IsNullOrWhiteSpace(p.Text)) return;
                _ui.Post(() =>
                {
                    if (p.Fraction >= 0) left.Update(p.Fraction, p.Phase);
                    else left.Clear();
                    // the apply line just says "Updating FINAL FANTASY XI..." again
                    var what = p.Phase == PatchPhase.Apply ? "" : $". {p.Text.TrimEnd('.')}";
                    SetRunStatus($"Updating FINAL FANTASY XI {step}{what}" + (eta.Length > 0 ? $". {eta}." : "."));
                });
            });
            try { await Task.Run(() => PolUpdates.UpdateFfxi(dirs.Ffxi, dirs.Viewer, dirs.Region, patch, token), token); }
            catch (Exception) when (token.IsCancellationRequested) { return await Stopped(); }
            catch (Exception ex)
            {
                Log.Error(ex, "Multi-account mode: FINAL FANTASY XI update failed");
                return await LetGo(AccountFailureKind.Install,
                                   ErrorText.Of(ex, "The FINAL FANTASY XI update didn't finish."));
            }
            finally { left.Clear(); }
            SetRunStatus($"{account.Name} is logged in. Starting the game {step}...");
        }
        if (token.IsCancellationRequested) return await Stopped();

        FfxiSettings.WriteFirstStartSettings(_links.PlayRegion());
        _links.WritePolOptions(account.ProfileId);
        if (await BackUpForRunAsync(hookProfile))
        {
            if (token.IsCancellationRequested) return await Stopped();
            SetRunStatus($"{account.Name} is logged in. Starting the game {step}...");
        }
        try { _pol.SignalPlay(); }
        catch (Exception ex)
        {
            Log.Error(ex, "Multi-account mode: could not signal Play for {Account}", account.ProfileId);
            return ex is CredentialKeyUnavailableException
                ? await LetGo(AccountFailureKind.Install, ex.Message)
                : await LetGo(AccountFailureKind.Account, "Provenance couldn't tell PlayOnline to start the game.");
        }
        if (await _pol.WaitForPlayAnswerAsync(MainViewModel.PlayAnswerTimeout) is { } playFailed)
        {
            Log.Error("Multi-account mode: the game did not start for {Account}: {Why}", account.ProfileId, playFailed);
            return await LetGo(AccountRules.PlayFailureKind(playFailed), playFailed);
        }

        var exited = _pol.WaitForExitAsync();
        _pol.ReleasePlayedSession();
        _ = NoteGameEndAsync(account, exited);
        Log.Information("Multi-account mode: {Account} is in the game", account.ProfileId);
        return null;
    }

    // once a run, before its first game starts. True when it was this account's turn
    internal async Task<bool> BackUpForRunAsync(string? accountBoot)
    {
        if (_runBackedUp) return false;
        _runBackedUp = true;
        await _links.BackUpGameSettings(accountBoot, SetRunStatus);
        return true;
    }

    // the run's moved on by the time a game that didn't start ends its pol.exe, so that's told when it happens (or
    // with the run's own report, if it's still going)
    private async Task NoteGameEndAsync(MultiRunAccount account, Task<int?> exited)
    {
        try
        {
            var code = await exited;
            if (PolSession.GameEndedText(code) is not { } text) return;
            Log.Error("Multi-account mode: FINAL FANTASY XI didn't start for {Account} (pol.exe exit code {Code})", account.ProfileId, code);
            var line = $"{account.Name}: {text}";
            if (Running) _lateGameFailures.Add(line);
            else
            {
                // the window may well be in the tray by now
                RequestShow?.Invoke();
                _dialogs.Error("Multi-account mode", line, offerLogs: true);
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Multi-account mode: couldn't tell how {Account}'s game ended", account.ProfileId); }
    }

    // never throws. StopHold leaves a session that got to the game alone, so that one's just forgotten.
    private async Task LetGoOfAccountAsync()
    {
        try
        {
            _pol.ReleaseClaim();
            await Task.Run(() =>
            {
                _pol.StopHold();
                _pol.ReleasePlayedSession();
            });
        }
        catch (Exception ex) { Log.Error(ex, "Multi-account mode: letting go of the account failed"); }
    }
}
