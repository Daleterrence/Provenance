using Provenance.Common.Game;
using Provenance.Common.Login;
using Provenance.Common.Patcher;
using Provenance.Common.Progress;
using Provenance.Common.Settings;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Serilog;

namespace Provenance.Wpf.ViewModels.Main;

internal sealed record LeanStartLinks(
    Func<bool> GameInstalled,
    Action<SessionPhase> SetPhase,
    Action<LaunchProgress> Report,
    Action<string, string> NewProgress,
    Action EndCancellable,
    Func<string?, Action<string>?, Task> BackUpGameSettings,
    Action<string> StartSocial,
    Action<PolSocialSnapshot> ApplySnapshot,
    Func<GameCheck?, Task> Play,
    Action ShowFull,
    Action Exit);

internal sealed record LeanEnd(int Code, string? Failure, bool Handled);

internal sealed class LeanStartViewModel(IDialogs dialogs, IWindowManager windows, ISettingsService settings, PolSession pol, UiThread ui,
                                         AccountsViewModel accounts, GameLaunch launch, MainViewModel main, LeanStartLinks links)
{
    private const string CantStart = "Can't start FINAL FANTASY XI";
    private const string NoticeTitle = "PlayOnline account notice";

    private bool _lean, _shown, _madeHandle;
    private GameCheck? _check;
    private PolSocialSnapshot? _held;
    private Func<Task>? _next;

    internal bool Wanted => accounts.StartGameImmediately && links.GameInstalled() && launch.GameFolders() is not null;

    internal bool Running => _lean;

    internal bool Open()
    {
        _madeHandle = false;
        _check = null;
        _held = null;
        _next = null;
        _lean = _shown = Wanted;
        if (!_shown) return false;
        // one that's only up for a few seconds was about something before this login, and would read as its own here
        if (main.Notice.Current is { Stays: false }) main.Notice.Take();
        windows.ShowLeanStart(main);
        return true;
    }

    internal void ShowFull()
    {
        if (!_shown) return;
        _shown = false;
        windows.CloseLeanStart();
        links.ShowFull();
    }

    internal void Finish()
    {
        _lean = false;
        ShowFull();
    }

    // a lean session's snapshots never reach the friends side, which mustn't write to its hook
    internal bool Hold(PolSocialSnapshot snap)
    {
        if (!_lean) return false;
        _held = snap;
        accounts.LearnIdentity(snap);
        return true;
    }

    internal void HandleAsked()
    {
        if (!_lean) return;
        _madeHandle = true;
        ShowFull();
    }

    // on the login's worker thread. The game's own check, and a no once the full window has to take over
    internal Func<bool>? BeforePol(string profileId, CancellationToken ct)
    {
        if (!_lean) return null;
        return () =>
        {
            var said = new InlineProgress<PatchProgress>(p => ui.Post(() => links.Report(new LaunchProgress("Checking", p.Text))));
            if (launch.CheckGame(said, ct) is not { } check) return true;
            _check = check;
            if (check.UpToDate) return true;
            Log.Information("The game needs an update or couldn't be checked, so the login carries on in the full window");
            // before the first snapshot can land, so the friends side's ready for it
            ui.Run(() =>
            {
                Finish();
                links.StartSocial(profileId);
                _next = PlayAsync;
            });
            return false;
        };
    }

    private GameCheck? TakeCheck()
    {
        var check = _check;
        _check = null;
        return check;
    }

    // Play's pressed for them, so the Play card's notice line would go unseen behind the update and the game
    private Task PlayAsync()
    {
        if (main.Social.AccountNotice is not { } notice) return links.Play(TakeCheck());
        dialogs.Info(NoticeTitle, notice);
        return Task.CompletedTask;
    }

    internal async Task FollowUpAsync()
    {
        var next = _next;
        _next = null;
        if (next is not null) await next();
        _check = null;
    }

    internal async Task<LeanEnd> AfterLogInAsync(string profileId, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return new(LoginCodes.Cancelled, null, false);
        var snap = _held ?? (pol.Pid is { } pid ? SocialSnapshotFiles.TryRead(pid) : null);
        _next = Fallback(snap);
        if (_next is null)
        {
            if (await StartGameAsync(profileId) is { } end) return end;
            ct = CancellationToken.None;
        }
        links.Report(new LaunchProgress("Logged in", "Loading your friends and profile..."));
        var full = await launch.GoFullAsync(ct);
        if (full.Code != 0)
        {
            _next = null;
            return new(full.Code, full.Text, false);
        }
        _lean = false;
        links.StartSocial(profileId);
        if (full.Snapshot is { } now) links.ApplySnapshot(now);
        return new(0, null, false);
    }

    // null when the game can start as it is. A handle's only active once the session's online, so the link needs a full one
    private Func<Task>? Fallback(PolSocialSnapshot? snap)
    {
        if (snap is null) return Told(() => dialogs.Error(CantStart, AccountRules.PlayGateFailure(null)!.Text));
        if (PolAccountNotices.For(snap.Notice, settings.RegionOrDefault, snap.NoticeDays) is { } notice)
            return Told(() => dialogs.Info(NoticeTitle, $"{notice.Text} ({notice.Code})"));
        if (AccountRules.PlayGateFailure(snap) is { } gate) return Told(() => dialogs.Error(CantStart, gate.Text));
        if (_madeHandle || snap.ContentIds.Any(c => c.Type == ContentIdType.Ffxi && c.Handle < 0)) return PlayAsync;
        return null;
    }

    private static Func<Task> Told(Action tell) => () =>
    {
        tell();
        return Task.CompletedTask;
    };

    // null when Play couldn't even be sent, which leaves a lean session held with nothing to show it on
    private async Task<LeanEnd?> StartGameAsync(string profileId)
    {
        // nothing's left to cancel, and a close or a Windows log-off mustn't wait on a login that's over
        links.EndCancellable();
        var notice = await launch.FinishWindowerUpdateAsync(text => links.NewProgress("Starting FINAL FANTASY XI", text));
        if (notice is not null) links.Report(LaunchProgress.Notice(notice));
        launch.WriteOptions(profileId);
        links.NewProgress("Starting FINAL FANTASY XI", "Starting FINAL FANTASY XI...");
        await links.BackUpGameSettings(null, text => links.Report(new LaunchProgress("Starting game", text)));
        links.Report(new LaunchProgress("Starting game", "Starting FINAL FANTASY XI...", 0.5));
        var signal = launch.SignalPlay();
        if (!signal.Sent)
        {
            Log.Error(signal.Error, "Could not signal Play");
            var why = signal.KeyProblem ?? "Provenance couldn't tell PlayOnline to start the game. Try again.";
            _next = Told(() => dialogs.Error(CantStart, why, offerLogs: true));
            return null;
        }
        links.SetPhase(SessionPhase.StartingGame);
        links.SetPhase(SessionPhase.InGame);
        if (await launch.WaitForPlayAnswerAsync() is { } failed)
        {
            links.SetPhase(SessionPhase.LoggedOut);
            pol.Prestarter.Prestart();
            Finish();
            dialogs.Error(CantStart, failed);
            return new(0, null, true);
        }
        try { if (pol.Pid is { } game) await GameWindow.BringForwardWhenUpAsync(game); }
        catch (Exception ex) { Log.Warning(ex, "Could not bring the game window to the front"); }
        _shown = false;
        windows.CloseLeanStart();
        links.EndCancellable();
        var exit = await pol.WaitForExitAsync();
        pol.GameExited();
        if (HookExitText.GameEndedText(exit) is not { } ended)
        {
            links.Exit();
            return new(0, null, true);
        }
        Log.Error("FINAL FANTASY XI didn't start (pol.exe exit code {Code})", exit);
        links.SetPhase(SessionPhase.LoggedOut);
        _lean = false;
        links.ShowFull();
        pol.Prestarter.Prestart();
        dialogs.Error(CantStart, ended, offerLogs: true);
        return new(0, null, true);
    }
}
