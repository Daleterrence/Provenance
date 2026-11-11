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

// what Play needs from the main window's own progress card and windows
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
    // the game update Play runs is handed the check a lean start already made, and nothing else ever is
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
        // POL's own Play gate, and it only asks for each list at login so there's no reload here either
        if (main.Social.PlayBlockedReason is { } why)
        {
            dialogs.Error("Can't start FINAL FANTASY XI", why);
            return;
        }
        if (!windows.ConfirmDropProfileEditor("Play", leaveSaving: true)) return;
        // nothing new goes to the hook from here, the game update still applies its answers
        links.SetPhase(SessionPhase.UpdatingGame);
        // like the viewer, the game update comes after the login with the session held meanwhile
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
        // the link waits for a create or delete that's going, which can come back not confirmed or not come back in time
        if ((main.Social.PlayBlockedReason ?? (main.Handles.ListEditGoing ? HookFailures.Handles.PlayWaits : null)) is { } held)
        {
            links.SetPhase(SessionPhase.LoggedIn);
            dialogs.Error("Can't start FINAL FANTASY XI", held);
            return;
        }
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

    // a test says whether its made-up session is still there
    internal Func<int, bool>? SessionAliveForTest { get; set; }

    internal Task<PreLaunchResult> LinkBeforePlayAsync()
    {
        links.NewProgress("Starting FINAL FANTASY XI", "Starting FINAL FANTASY XI...");
        return launch.LinkAsync(
            ask: text => Asking(() => dialogs.YesNo(PreLaunchLink.Title, text)),
            failed: text => Asking(() =>
            {
                dialogs.Error(PreLaunchLink.Title, text);
                return true;
            }),
            saying: text => main.ProgressText = text,
            pageWaiting: () => main.Handles.Busy ? main.Handles.Note.Text : null,
            alive: SessionAliveForTest,
            read: _ => main.Social.Last);
    }

    // the footer's "has to wait till the game's closed" isn't true while the answer can still stop it starting
    private bool Asking(Func<bool> dialog)
    {
        var note = main.Social.StatusNote;
        main.Social.StatusNote = "";
        try { return dialog(); }
        finally { if (main.Social.StatusNote == "") main.Social.StatusNote = note; }
    }

    // a test points it at made-up folders
    internal Func<LauncherSettings, string?, BackupSources?>? GameBackupSourcesForTest { get; set; }

    // however it goes the game still starts, a failed backup only gets a notice
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
