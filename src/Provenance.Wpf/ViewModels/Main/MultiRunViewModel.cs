using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common;
using Provenance.Common.Credentials;
using Provenance.Common.Game;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Provenance.Common.Login;
using Provenance.Common.Patcher;
using Provenance.Common.Progress;
using Provenance.Common.Settings;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Serilog;

namespace Provenance.Wpf.ViewModels.Main;

internal sealed record MultiRunLinks(
    Action<SessionPhase> SetPhase,
    Func<bool> EnsureMemberAgreement,
    Func<string?> PromptHandleName,
    Func<string?, Action<string>?, Task> BackUpGameSettings,
    Action<string> Notice);

public partial class MultiRunViewModel : ObservableObject, IMultiRunHost
{
    private readonly IDialogs _dialogs;
    private readonly ISettingsService _settings;
    private readonly ICredentialService _credentials;
    private readonly PolSession _pol;
    private readonly UiThread _ui;
    private readonly AccountsViewModel _accounts;
    private readonly SessionState _session;
    private readonly GameLaunch _launch;
    private readonly MultiRunLinks _links;

    public event Action? RequestShow;
    public event Action? RequestExit;

    public const string AllInGameText = "Every account enabled for Multi-account login is already in the game. Nothing was started.";

    internal MultiRunViewModel(IDialogs dialogs, ISettingsService settings, ICredentialService credentials,
                               PolSession pol, UiThread ui, AccountsViewModel accounts, SessionState session, GameLaunch launch, MultiRunLinks links)
    {
        _dialogs = dialogs;
        _settings = settings;
        _credentials = credentials;
        _pol = pol;
        _ui = ui;
        _accounts = accounts;
        _session = session;
        _launch = launch;
        _links = links;
        session.PhaseChanged += OnPhaseChanged;
    }

    private static readonly TimeSpan NoProgressLimit = TimeSpan.FromMinutes(10);

    private List<MultiAccountEntry> MultiEntries() =>
        MultiAccountRules.RunEntries(_settings.Current.MultiAccounts, _accounts.Profiles.Select(p => p.Id));

    public int AccountCount => MultiEntries().Count;
    public bool HasAccounts => Running || (AccountCount > 0 && MultiPlatformChosen());
    public string StartAllText => Stopping ? "STOPPING..." : Running ? "STOP ALL ACCOUNTS" : $"START ALL ACCOUNTS ({AccountCount})";

    public bool Running => _session.InMultiRun;

    public bool Stopping => _session.Phase == SessionPhase.MultiStopping;

    private LastRaised _runningShown, _stoppingShown;

    private void OnPhaseChanged()
    {
        bool runningChanged = _runningShown.Changed(Running), stoppingChanged = _stoppingShown.Changed(Stopping);
        if (!runningChanged && !stoppingChanged) return;
        if (runningChanged) OnPropertyChanged(nameof(Running));
        if (stoppingChanged) OnPropertyChanged(nameof(Stopping));
        OnPropertyChanged(nameof(StartAllText));
        OnPropertyChanged(nameof(HasAccounts));
        StartAllAccountsCommand.NotifyCanExecuteChanged();
    }

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

    private bool CanStartOrStop() => Running ? !Stopping : !_session.Launching && !_session.LoggedIn && _pol.Pid is null;

    internal Func<bool> UpdateBusy { get; set; } = () => false;

    private bool _atOpen;

    internal void StartAtOpen()
    {
        _atOpen = true;
        StartAllAccountsCommand.Execute(null);
    }

    [RelayCommand(CanExecute = nameof(CanStartOrStop), AllowConcurrentExecutions = true)]
    private async Task StartAllAccountsAsync()
    {
        bool atOpen = _atOpen;
        _atOpen = false;
        if (Running) { StopAllAccounts(); return; }
        if (UpdateBusy()) return;
        if (_session.Launching || _session.LoggedIn || _pol.Pid is not null) return;
        if (!MultiPlatformChosen())
        {
            Log.Warning("Multi-account mode: not started, Windower or Ashita is not chosen");
            return;
        }
        if (!_links.EnsureMemberAgreement()) return;
        var entries = MultiEntries();
        if (entries.Count == 0) return;
        var accounts = entries.Select(e => _accounts.Profiles.First(p => p.Id == e.ProfileId))
                              .Select(p => new MultiRunAccount(p.Id, _accounts.NameOf(p)))
                              .ToList();
        if (MultiAccountRules.WithoutProfile(entries) is { Count: > 0 } unpicked)
        {
            Log.Warning("Multi-account mode: not started, {Count} account(s) have no Windower profile or Ashita boot configuration picked", unpicked.Count);
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
        _links.SetPhase(SessionPhase.MultiRun);
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
            _links.SetPhase(SessionPhase.LoggedOut);
            Status = "";
            _cts.Dispose();
            _cts = null;
            RunSnapshot = null;
            _runAccounts = [];
            _runHookProfiles = new();
            _runPlatform = ThirdPartyHook.None;
            _session.NoteRunChanged();
        }
        var failures = result?.Failures ?? [];
        Log.Information("Multi-account mode: finished, {Started} started, {Already} already in the game, {Failures} failure(s), stopped {Stopped}",
                        result?.Started, result?.AlreadyInGame, failures.Count, result?.Stopped);
        if (_exitAfterStop) { RequestExit?.Invoke(); return; }
        if (failures.Count > 0 || unexpected != null || _lateGameFailures.Count > 0)
        {
            var lines = failures.Select(f => $"{f.Account.Name}: {f.Failure.Text}").Concat(_lateGameFailures).ToList();
            _lateGameFailures.Clear();
            if (unexpected != null) lines.Add(unexpected);
            RequestShow?.Invoke();
            _dialogs.Error("Multi-account mode", string.Join("\n", lines), offerLogs: true);
            return;
        }
        if (result is { CloseApp: true }) RequestExit?.Invoke();
        else if (result is { Started: 0, AlreadyInGame: > 0, Stopped: false })
        {
            if (atOpen) _links.Notice(AllInGameText);
            else _dialogs.Info("Multi-account mode", AllInGameText);
        }
    }

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
        _session.NoteRunChanged();
    }

    public void StopAllAccounts()
    {
        if (!Running || Stopping || _cts is null) return;
        Log.Warning("Multi-account mode: stop");
        _links.SetPhase(SessionPhase.MultiStopping);
        Status = "Stopping...";
        _cts.Cancel();
    }

    internal void HoldRun(CancellationTokenSource cts) => _cts = cts;
    internal bool ExitsAfterStop => _exitAfterStop;
    internal void RaiseExit() => RequestExit?.Invoke();

    internal bool ConfirmStopAccounts() => _dialogs.Confirm("Multi-account mode",
        "Your accounts are still starting. Stop them and close Provenance?", "STOP AND CLOSE", ConfirmStyle.Destructive);

    public void StopAllAccountsAndExit()
    {
        if (!Running) return;
        _exitAfterStop = true;
        StopAllAccounts();
    }

    internal void SetRunStatus(string text)
    {
        if (Running && !Stopping) Status = text;
    }

    bool IMultiRunHost.IsInGame(string profileId) => SessionRecords.GameRunningFor(profileId);

    async Task<AccountFailure?> IMultiRunHost.LogInAndPlayAsync(MultiRunAccount account, CancellationToken ct)
    {
        try
        {
            return await _ui.RunAsync(() => RunAccountAsync(account, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
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
            return new AccountFailure(ex is CredentialKeyUnavailableException ? AccountFailureKind.Install : AccountFailureKind.Account,
                                      ErrorText.Of(ex, "Provenance ran into a problem with this account."));
        }
        if (failure is null) return null;
        try { ApplyFailure(account, failure); }
        catch (Exception ex) { Log.Error(ex, "Multi-account mode: could not record the failure of {Account}", account.ProfileId); }
        return failure;
    }

    private async Task<AccountFailure?> RunOneAccountAsync(MultiRunAccount account, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int number = Math.Max(1, _runAccounts.ToList().FindIndex(a => a.ProfileId == account.ProfileId) + 1);
        string step = $"({number} of {_runAccounts.Count})";

        if (!MultiPlatformChosen())
            return new(AccountFailureKind.Install, "You must use either Windower or Ashita with Provenance to use multi-account login.");
        var hookProfile = _pol.Platform == _runPlatform ? _runHookProfiles.GetValueOrDefault(account.ProfileId) : null;
        if (_pol.Platform == ThirdPartyHook.Ashita && hookProfile is { Length: > 0 } boot
            && !AshitaBootFiles.IsBootFileName(boot))
            return new(AccountFailureKind.Account, AshitaLauncher.AccountBootFileProblem);

        var profile = _accounts.Profiles.FirstOrDefault(p => p.Id == account.ProfileId);
        if (profile is null) return AccountRules.MissingDetailsFailure(false);
        var details = AccountRules.PrepareLogIn(_credentials, profile.Id);
        if (details.Missing) return AccountRules.MissingDetailsFailure(_credentials.HasEntries(profile.Id));
        if (details.Problem is { } problem) return new(AccountFailureKind.Account, problem);
        var (pol, squareEnix) = (details.Pol!, details.SquareEnix!);

        var claim = _launch.Claim(profile, () =>
        {
            var code = _accounts.AskOtp(profile, squareEnix);
            return ct.IsCancellationRequested ? null : code;
        });
        if (claim.InUse is { } inUse) return new(AccountFailureKind.Account, inUse);
        if (claim.OtpCancelled)
        {
            ct.ThrowIfCancellationRequested();
            return new(AccountFailureKind.Account, "The one-time password was cancelled.");
        }
        string otp = claim.Otp!;

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
        LogInResult logIn;
        try
        {
            logIn = await _launch.LogInAsync(new LaunchLogin(profile, pol, squareEnix, otp, _accounts.EnsureHomeSlot(profile), true, hookProfile,
                                                             progress, AskHandleName), token);
        }
        catch (Exception) when (token.IsCancellationRequested) { return await Stopped(); }
        if (logIn.Code != 0)
        {
            await LetGoOfAccountAsync();
            if (logIn.Code == LoginCodes.Cancelled && token.IsCancellationRequested) return await Stopped();
            var kind = AccountFailures.Classify(logIn.Code, logIn.Refusal, profile.UseOtp);
            return new(kind, lastFailure ?? $"The login didn't finish (code {logIn.Code}).");
        }
        SetRunStatus($"{account.Name} is logged in. Starting the game {step}...");

        var read = _pol.Pid is { } pid ? SocialSnapshotFiles.TryRead(pid) : null;
        var snap = RunSnapshot ?? read;
        // this pol.exe's own file first, so it can't be the account before's
        if ((read ?? snap) is { } own) _accounts.LearnIdentity(profile.Id, own);
        if (AccountRules.PlayGateFailure(snap) is { } gate) return await LetGo(gate.Kind, gate.Text);
        if (token.IsCancellationRequested) return await Stopped();

        if (_launch.GameFolders() is not null)
        {
            string eta = "";
            var left = new PhaseTimeLeft(text => eta = text);
            var patch = new InlineProgress<PatchProgress>(p =>
            {
                Kick();
                if (string.IsNullOrWhiteSpace(p.Text)) return;
                _ui.Post(() =>
                {
                    if (p.Fraction >= 0) left.Update(p.Fraction, p.Phase);
                    var what = p.Phase == PatchPhase.Apply ? "" : $". {p.Text.TrimEnd('.')}";
                    SetRunStatus($"Updating FINAL FANTASY XI {step}{what}" + (eta.Length == 0 ? "." : eta.EndsWith("...", StringComparison.Ordinal) ? $". {eta}" : $". {eta}."));
                });
            });
            GameUpdateResult update;
            try { update = await _launch.UpdateGameAsync(patch, token); }
            finally { left.Clear(); }
            if (!update.Done)
            {
                if (token.IsCancellationRequested) return await Stopped();
                Log.Error(update.Error, "Multi-account mode: FINAL FANTASY XI update failed");
                return await LetGo(AccountFailureKind.Install,
                                   ErrorText.Of(update.Error!, "The FINAL FANTASY XI update didn't finish."));
            }
            SetRunStatus($"{account.Name} is logged in. Starting the game {step}...");
        }
        if (token.IsCancellationRequested) return await Stopped();

        await _launch.FinishWindowerUpdateAsync(_ => SetRunStatus($"{WindowerLauncher.FinishingUpdateText} {step}"), token);
        if (token.IsCancellationRequested) return await Stopped();
        _launch.WriteOptions(account.ProfileId);
        if (await BackUpForRunAsync(hookProfile))
        {
            if (token.IsCancellationRequested) return await Stopped();
            SetRunStatus($"{account.Name} is logged in. Starting the game {step}...");
        }
        var signal = _launch.SignalPlay();
        if (!signal.Sent)
        {
            Log.Error(signal.Error, "Multi-account mode: could not signal Play for {Account}", account.ProfileId);
            return signal.KeyProblem is { } keyProblem
                ? await LetGo(AccountFailureKind.Install, keyProblem)
                : await LetGo(AccountFailureKind.Account, "Provenance couldn't tell PlayOnline to start the game.");
        }
        if (await _launch.WaitForPlayAnswerAsync() is { } playFailed)
        {
            Log.Error("Multi-account mode: the game did not start for {Account}: {Why}", account.ProfileId, playFailed);
            return await LetGo(AccountRules.PlayFailureKind(playFailed), playFailed);
        }

        await HoldForGameAsync(account, token);
        var exited = _pol.WaitForExitAsync();
        _pol.ReleasePlayedSession();
        _ = NoteGameEndAsync(account, exited);
        Log.Information("Multi-account mode: {Account} is in the game", account.ProfileId);
        return null;
    }

    internal Func<int, TimeSpan, CancellationToken, Task<bool>> GameUp { get; set; } = GameWindow.WaitUntilUpAsync;

    private async Task HoldForGameAsync(MultiRunAccount account, CancellationToken token)
    {
        IReadOnlyDictionary<string, string?> profiles = _pol.Platform == _runPlatform ? _runHookProfiles : new Dictionary<string, string?>();
        if (_pol.Pid is not { } game ||
            !MultiAccountRules.NextWaitsForGame(_pol.Platform, _runAccounts, account.ProfileId, profiles, SessionRecords.GameRunningFor))
            return;
        var held = Stopwatch.StartNew();
        bool up = await GameUp(game, MultiAccountRules.GameUpLimit, token);
        Log.Information("Multi-account mode: the next account waited {Ms}ms for {Account}'s game to take its Windower settings (window up: {Up})",
                        held.ElapsedMilliseconds, account.ProfileId, up);
    }

    internal async Task<bool> BackUpForRunAsync(string? accountBoot)
    {
        if (_runBackedUp) return false;
        _runBackedUp = true;
        await _links.BackUpGameSettings(accountBoot, SetRunStatus);
        return true;
    }

    private async Task NoteGameEndAsync(MultiRunAccount account, Task<int?> exited)
    {
        try
        {
            var code = await exited;
            if (HookExitText.GameEndedText(code) is not { } text) return;
            Log.Error("Multi-account mode: FINAL FANTASY XI didn't start for {Account} (pol.exe exit code {Code})", account.ProfileId, code);
            var line = $"{account.Name}: {text}";
            if (Running) _lateGameFailures.Add(line);
            else
            {
                RequestShow?.Invoke();
                _dialogs.Error("Multi-account mode", line, offerLogs: true);
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Multi-account mode: couldn't tell how {Account}'s game ended", account.ProfileId); }
    }

    private async Task LetGoOfAccountAsync()
    {
        try
        {
            _pol.Records.ReleaseClaim();
            await Task.Run(() =>
            {
                _pol.StopHold();
                _pol.ReleasePlayedSession();
            });
        }
        catch (Exception ex) { Log.Error(ex, "Multi-account mode: letting go of the account failed"); }
    }
}
