using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common;
using Provenance.Common.Credentials;
using Provenance.Common.Game;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Login;
using Provenance.Common.Progress;
using Provenance.Common.Settings;
using Serilog;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Social;
using Provenance.Wpf.Views.Dialogs;
using Provenance.Wpf.ViewModels.Settings;

namespace Provenance.Wpf.ViewModels.Main;

public partial class MainViewModel : IMultiRunHost
{
    // each progress report restarts the clock, so the first account can download an update of any size
    private static readonly TimeSpan NoProgressLimit = TimeSpan.FromMinutes(10);

    private List<MultiAccountEntry> MultiEntries() =>
        MultiAccountRules.RunEntries(_settings.Current.MultiAccounts, Profiles.Select(p => p.Id));

    public int MultiAccountCount => MultiEntries().Count;
    public bool HasMultiAccounts => MultiRunning || (MultiAccountCount > 0 && MultiPlatformChosen());
    public string StartAllText => MultiStopping ? "STOPPING..." : MultiRunning ? "STOP ALL ACCOUNTS" : $"START ALL ACCOUNTS ({MultiAccountCount})";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartAllText), nameof(HasMultiAccounts))]
    [NotifyCanExecuteChangedFor(nameof(StartAllAccountsCommand), nameof(SignInCommand))]
    private bool _multiRunning;

    partial void OnMultiRunningChanged(bool value)
    {
        Social.GroupChatBlocked = value;
        RefreshRepair();
        if (!value) _ = ProbeRepairAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartAllText))]
    [NotifyCanExecuteChangedFor(nameof(StartAllAccountsCommand))]
    private bool _multiStopping;

    [ObservableProperty] private string _multiStatus = "";

    private CancellationTokenSource? _multiCts;
    private bool _exitAfterStop;

    private IReadOnlyList<MultiRunAccount> _runAccounts = [];
    private Dictionary<string, string?> _runHookProfiles = new();
    private string _runPlatform = "";

    private PolSocialSnapshot? _runSnapshot;
    private readonly List<string> _lateGameFailures = [];
    private bool _runBackedUp;

    private SettingsViewModel? _openSettings;

    private void RefreshMultiAccounts()
    {
        OnPropertyChanged(nameof(MultiAccountCount));
        OnPropertyChanged(nameof(HasMultiAccounts));
        OnPropertyChanged(nameof(StartAllText));
    }

    private bool MultiPlatformChosen() =>
        _pol.Platform is ThirdPartyHook.Windower or ThirdPartyHook.Ashita;

    private bool CanStartOrStop() => MultiRunning ? !MultiStopping : !IsLaunching && !SocialConnected && _pol.Pid is null;

    // Pid has no change notice, so this gets called wherever it changes (any thread)
    private void RefreshStartAll() =>
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            StartAllAccountsCommand.NotifyCanExecuteChanged();
            RefreshRepair();
        });

    // concurrent, so the same button stays clickable as the stop while a run goes
    [RelayCommand(CanExecute = nameof(CanStartOrStop), AllowConcurrentExecutions = true)]
    private async Task StartAllAccountsAsync()
    {
        if (MultiRunning) { StopAllAccounts(); return; }
        if (IsLaunching || SocialConnected || _pol.Pid is not null) return;
        if (!MultiPlatformChosen())
        {
            Log.Warning("Multi-account mode: not started, Windower or Ashita is not chosen");
            return;
        }
        var owner = Application.Current.MainWindow;
        if (!EnsureMemberAgreement(owner)) return;
        var entries = MultiEntries();
        if (entries.Count == 0) return;
        var accounts = entries.Select(e => Profiles.First(p => p.Id == e.ProfileId))
                              .Select(p => new MultiRunAccount(p.Id, string.IsNullOrWhiteSpace(p.HandleName) ? p.DisplayName : p.HandleName!))
                              .ToList();
        if (MultiAccountRules.WithoutProfile(entries) is { Count: > 0 } unpicked)
        {
            Log.Warning("Multi-account mode: not started, {Count} account(s) have no profile picked", unpicked.Count);
            var names = unpicked.Select(e => accounts.First(a => a.ProfileId == e.ProfileId).Name).ToList();
            MessageDialog.ShowError(owner, "Multi-account mode", MultiAccountRules.NoProfileText(_pol.Platform, names));
            return;
        }
        _runAccounts = accounts;
        _runHookProfiles = entries.ToDictionary(e => e.ProfileId, e => e.ThirdPartyHookProfile);
        _runPlatform = _pol.Platform;
        _exitAfterStop = false;
        _runBackedUp = false;
        _lateGameFailures.Clear();
        // a friends window left open mustn't write requests to the run's lean sessions
        _socialPoll.Stop();
        CloseSocialWindows();
        MultiRunning = true;
        _multiCts = new CancellationTokenSource();
        var options = new MultiRunOptions(_settings.Current.MultiContinueOnError, _settings.Current.MultiCloseWhenFinished);
        var status = new Progress<string>(SetRunStatus);
        Log.Information("Multi-account mode: starting {Count} account(s)", accounts.Count);
        MultiRunResult? result = null;
        string? unexpected = null;
        try { result = await MultiAccountRunner.RunAsync(accounts, options, this, status, _multiCts.Token); }
        catch (Exception ex)
        {
            Log.Error(ex, "Multi-account mode: the run ended with an error");
            unexpected = ErrorText.Of(ex, "Provenance stopped starting your accounts because of a problem it didn't expect.");
        }
        finally
        {
            MultiRunning = false;
            MultiStopping = false;
            MultiStatus = "";
            _multiCts.Dispose();
            _multiCts = null;
            _runSnapshot = null;
            _runAccounts = [];
            _runHookProfiles = new();
            _runPlatform = "";
            _openSettings?.MultiRunChanged();
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
            MessageDialog.ShowError(owner, "Multi-account mode", string.Join("\n", lines), offerLogs: true);
            return;
        }
        if (result is { CloseApp: true }) RequestExit?.Invoke();
    }

    // saved straight away so a crash can't lose it, and found by ID as Settings may have swapped the list mid-run
    private void ApplyFailure(MultiRunAccount account, AccountFailure failure)
    {
        Log.Information("Multi-account mode: {Account} failed ({Kind}): {Text}", account.ProfileId, failure.Kind, failure.Text);
        if (failure.Kind is not (AccountFailureKind.Credential or AccountFailureKind.AccountBlocked)) return;
        if (_settings.Current.MultiAccounts.FirstOrDefault(e => e.ProfileId == account.ProfileId) is not { } entry) return;
        entry.Enabled = false;
        entry.Problem = failure.Text;
        if (failure.Kind == AccountFailureKind.Credential) entry.CredentialsRefused = true;
        _settings.Save(_settings.Current);
        RefreshMultiAccounts();
        _openSettings?.MultiRunChanged();
    }

    public void StopAllAccounts()
    {
        if (!MultiRunning || MultiStopping || _multiCts is null) return;
        Log.Warning("Multi-account mode: stop");
        MultiStopping = true;
        MultiStatus = "Stopping...";
        _multiCts.Cancel();
    }

    public void StopAllAccountsAndExit()
    {
        if (!MultiRunning) return;
        _exitAfterStop = true;
        StopAllAccounts();
    }

    private void SetRunStatus(string text)
    {
        if (MultiRunning && !MultiStopping) MultiStatus = text;
    }

    bool IMultiRunHost.IsInGame(string profileId) => PolSession.GameRunningFor(profileId);

    async Task<AccountFailure?> IMultiRunHost.SignInAndPlayAsync(MultiRunAccount account, CancellationToken ct)
    {
        try
        {
            return await Application.Current.Dispatcher.InvokeAsync(() => RunAccountAsync(account, ct)).Task.Unwrap().ConfigureAwait(false);
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
        try { failure = await SignInAndPlayAsync(account, ct); }
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
    private async Task<AccountFailure?> SignInAndPlayAsync(MultiRunAccount account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var owner = Application.Current.MainWindow;
        int number = Math.Max(1, _runAccounts.ToList().FindIndex(a => a.ProfileId == account.ProfileId) + 1);
        string step = $"({number} of {_runAccounts.Count})";

        if (!MultiPlatformChosen())
            return new(AccountFailureKind.Install, "You must use either Windower or Ashita with Provenance to use multi-account login.");
        // after a switch between Windower and Ashita mid-run a stored profile means nothing
        var hookProfile = _pol.Platform == _runPlatform ? _runHookProfiles.GetValueOrDefault(account.ProfileId) : null;
        if (_pol.Platform == ThirdPartyHook.Ashita && hookProfile is { Length: > 0 } boot
            && !AshitaLauncher.IsBootFileName(boot))
            return new(AccountFailureKind.Account, AshitaLauncher.AccountBootFileProblem);

        var profile = Profiles.FirstOrDefault(p => p.Id == account.ProfileId);
        if (profile is null) return AccountRules.MissingDetailsFailure(default);
        var details = AccountRules.PrepareSignIn(_credentials, profile.Id);
        if (details.Missing) return AccountRules.MissingDetailsFailure(_credentials.ReadIds(profile.Id));
        if (details.Problem is { } problem) return new(AccountFailureKind.Account, problem);
        var (pol, sea) = (details.Pol!, details.Sea!);

        if (!_pol.TryClaimAccount(profile.Id, out var claimProblem))
            return new(AccountFailureKind.Account, claimProblem ?? "This account is in use in another copy of Provenance.");

        // outside the no-progress guard, it waits for the user
        string otp = "";
        if (profile.UseOtp)
        {
            var code = AskOtp(owner, profile, sea);
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
        var progress = new InlineProgress<LaunchProgress>(p =>
        {
            Kick();
            Log.Information("progress: {Stage}: {Detail}", p.Stage, p.Detail);
            if (p.Stage == "Failed") lastFailure = p.Detail;
        });
        string? AskHandleName()
        {
            Pause();
            try { return PromptHandleName(); }
            finally { Kick(); }
        }
        _runSnapshot = null;
        int signIn;
        try
        {
            signIn = await _pol.ConnectAsync(pol, sea, otp, profile.Id, progress, AskHandleName, token,
                                             new SessionOptions(Lean: true, ThirdPartyHookProfile: hookProfile,
                                                                HomeSlot: HomeSlotFor(profile)));
        }
        catch (Exception) when (token.IsCancellationRequested) { return await Stopped(); }
        if (signIn != 0)
        {
            await LetGoOfAccountAsync();
            if (signIn == LoginCodes.Cancelled && token.IsCancellationRequested) return await Stopped();
            var kind = AccountFailures.Classify(signIn, _pol.LastRefusalReason, profile.UseOtp);
            return new(kind, lastFailure ?? $"The login didn't finish (code {signIn}).");
        }
        SetRunStatus($"{account.Name} is logged in. Starting the game {step}...");

        var snap = _runSnapshot ?? (_pol.Pid is { } pid ? SocialRequests.TryRead(pid) : null);
        if (AccountRules.PlayGateFailure(snap) is { } gate) return await LetGo(gate.Kind, gate.Text);
        if (token.IsCancellationRequested) return await Stopped();

        if (GameFolders() is { } dirs)
        {
            var left = new TimeLeft();   // UI thread only, like the status
            var patch = new InlineProgress<PatchProgress>(p =>
            {
                Kick();
                if (string.IsNullOrWhiteSpace(p.Text)) return;
                long now = Environment.TickCount64;
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    if (p.Fraction >= 0) left.Add(now, p.Fraction, 1);
                    else left.Reset();
                    var eta = left.Text;
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
            SetRunStatus($"{account.Name} is logged in. Starting the game {step}...");
        }
        if (token.IsCancellationRequested) return await Stopped();

        FfxiSettings.WriteFirstStartSettings(PlayRegion);
        WritePolOptions(account.ProfileId);
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
        if (await _pol.WaitForPlayAnswerAsync(PlayAnswerTimeout) is { } playFailed)
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
        await BackUpGameSettingsAsync(accountBoot, SetRunStatus);
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
            if (MultiRunning) _lateGameFailures.Add(line);
            else
            {
                // the window may well be in the tray by now
                RequestShow?.Invoke();
                MessageDialog.ShowError(Application.Current?.MainWindow, "Multi-account mode", line, offerLogs: true);
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
