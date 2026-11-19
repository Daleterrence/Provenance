using Provenance.Common;
using Provenance.Common.Game;
using Provenance.Common.Hook;
using Provenance.Common.Login;
using Provenance.Common.Patcher;
using Provenance.Common.Settings;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Serilog;

namespace Provenance.Wpf.ViewModels.Main;

internal sealed record GameStartLinks(
    Action<SessionPhase> SetPhase,
    Func<string, string, CancellationToken> BeginCancellable,
    Action EndCancellable,
    Action<string, string> NewProgress,
    Action<LaunchProgress> Report,
    Action ShowHandles,
    Action Hide,
    Action Show,
    Action Exit);

internal sealed class GameStart(IDialogs dialogs, IWindowManager windows, ISettingsService settings, PolSession pol, GameLaunch launch,
                                MainViewModel main, GameStartLinks links)
{
    private GameCheck? _knownCheck;

    internal async Task PlayWithAsync(GameCheck? known)
    {
        _knownCheck = known;
        try { if (main.PlayCommand.CanExecute(null)) await main.PlayCommand.ExecuteAsync(null); }
        finally { _knownCheck = null; }
    }

    internal async Task PlayAsync()
    {
        if (main.Multi.Running) return;
        if (main.Social.PlayBlockedReason is { } why)
        {
            dialogs.Error("Can't start FINAL FANTASY XI", why);
            return;
        }
        if (!windows.ConfirmDropProfileEditor("Play", leaveSaving: true)) return;
        links.SetPhase(SessionPhase.UpdatingGame);
        if (!await UpdateGameAsync())
        {
            links.SetPhase(SessionPhase.LoggedIn);
            return;
        }
        links.SetPhase(SessionPhase.StartingGame);
        if (await LinkBeforePlayAsync() is var linked and not PreLaunchResult.Play)
        {
            links.SetPhase(SessionPhase.LoggedIn);
            if (linked == PreLaunchResult.ShowHandles) links.ShowHandles();
            return;
        }
        if (main.Social.PlayBlockedReason is { } held)
        {
            links.SetPhase(SessionPhase.LoggedIn);
            dialogs.Error("Can't start FINAL FANTASY XI", held);
            return;
        }
        if (main.Handles.ListEditGoing && !dialogs.Confirm("Start FINAL FANTASY XI", HookFailures.Handles.PlayDiscards, "CONTINUE"))
        {
            links.SetPhase(SessionPhase.LoggedIn);
            return;
        }
        await FinishWindowerUpdateAsync();
        launch.WriteOptions(main.Accounts.SessionProfileId);
        links.NewProgress("Starting FINAL FANTASY XI", "Starting FINAL FANTASY XI...");
        await BackUpGameSettingsAsync(null, text => main.ProgressText = text);
        links.Report(new LaunchProgress("Starting game", "Starting FINAL FANTASY XI...", 0.5));
        var signal = launch.SignalPlay();
        if (!signal.Sent)
        {
            Log.Error(signal.Error, "Could not signal Play");
            links.SetPhase(SessionPhase.LoggedIn);
            dialogs.Error("Can't start FINAL FANTASY XI",
                signal.KeyProblem ?? "Provenance couldn't tell PlayOnline to start the game. Try again.", offerLogs: true);
            return;
        }
        links.SetPhase(SessionPhase.InGame);
        if (await launch.WaitForPlayAnswerAsync() is { } failed)
        {
            links.SetPhase(SessionPhase.LoggedOut);
            pol.Prestarter.Prestart();
            dialogs.Error("Can't start FINAL FANTASY XI", failed);
            return;
        }
        bool up = false;
        try { up = pol.Pid is { } game && await GameWindow.BringForwardWhenUpAsync(game); }
        catch (Exception ex) { Log.Warning(ex, "Could not bring the game window to the front"); }
        if (up) main.Handles.GameStarted();
        main.GameUp = true;
        links.Hide();
        main.ClockRunning = false;
        var exit = await pol.WaitForExitAsync();
        pol.GameExited();
        var ended = HookExitText.GameEndedText(exit);
        if (ended is not null) Log.Error("FINAL FANTASY XI didn't start (pol.exe exit code {Code})", exit);
        else if (settings.Current.ExitAfterGameExits) { links.Exit(); return; }
        links.SetPhase(SessionPhase.LoggedOut);
        links.Show();
        pol.Prestarter.Prestart();
        if (ended is not null) dialogs.Error("Can't start FINAL FANTASY XI", ended, offerLogs: true);
    }

    private async Task FinishWindowerUpdateAsync()
    {
        var notice = await launch.FinishWindowerUpdateAsync(text => links.NewProgress("Starting FINAL FANTASY XI", text));
        if (notice is not null) links.Report(LaunchProgress.Notice(notice));
    }

    internal Func<int, bool>? SessionAliveForTest { get; set; }

    internal Task<PreLaunchResult> LinkBeforePlayAsync()
    {
        links.NewProgress("Starting FINAL FANTASY XI", "Starting FINAL FANTASY XI...");
        return launch.LinkAsync(
            ask: text => dialogs.YesNo(PreLaunchLink.Title, text),
            failed: text => dialogs.Error(PreLaunchLink.Title, text),
            saying: text => main.ProgressText = text,
            pageWaiting: () => main.Handles.Busy ? main.Handles.Note.Text : null,
            alive: SessionAliveForTest,
            read: _ => main.Social.Last);
    }

    internal Func<LauncherSettings, string?, BackupSources?>? GameBackupSourcesForTest { get; set; }

    internal async Task BackUpGameSettingsAsync(string? accountBoot, Action<string>? saying = null)
    {
        if (await launch.BackUpAsync(accountBoot, saying, GameBackupSourcesForTest) is { } problem)
            main.Notice.Show(problem, "AlertCircleOutline", stays: true);
    }

    internal async Task<bool> UpdateGameAsync()
    {
        if (launch.GameFolders() is null) return true;
        var known = _knownCheck;
        _knownCheck = null;
        var ct = links.BeginCancellable("Updating FINAL FANTASY XI", "Checking for FINAL FANTASY XI updates...");
        var progress = new Progress<PatchProgress>(p => links.Report(LaunchProgress.Updating("Updating", p, 0.01, 0.99)));
        try
        {
            var result = await launch.UpdateGameAsync(progress, ct, known);
            if (result.Done) return true;
            if (result.Cancelled)
            {
                main.Notice.Show("The FINAL FANTASY XI update was stopped. Press Play to continue it.", "InformationOutline");
                return false;
            }
            var ex = result.Error!;
            Log.Error(ex, "FINAL FANTASY XI update failed");
            bool inUse = ex is UnauthorizedAccessException || ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);
            dialogs.Error("Couldn't update FINAL FANTASY XI",
                inUse ? "One of the FINAL FANTASY XI files is in use, so it couldn't be updated. Close every FINAL FANTASY XI window, then press Play again."
                      : ErrorText.Of(ex, "The FINAL FANTASY XI update didn't finish. Press Play to try again, and if it keeps happening, press Open logs to see what went wrong."),
                offerLogs: !ErrorText.Ours(ex));
            return false;
        }
        finally { links.EndCancellable(); }
    }
}
