using System.Diagnostics;
using System.IO;
using Provenance.Common.Security;
using Provenance.Common.Game;
using Provenance.Wpf.ViewModels.Main;
using Provenance.Wpf.Views.Install;
using Serilog;
using Velopack;
using Velopack.Locators;

namespace Provenance.Wpf.Services;

public static class SelfUpdate
{
    public const string UpdateServer = "https://updates.provenancexi.app/";

    internal const string WaitingText =
        "Provenance needs to update, you can either restart the app to do it now, or it will happen the next time it's started.";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(2);
    // reopening from the tray over and over only asks the server once in this long
    private static readonly TimeSpan QuietGap = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(30);

    // Update.exe kills everything running from the install folder before it swaps the files, and a pol.exe holding
    // pol_hook.dll stops the swap anyway
    private static readonly string[] InUseNames = ["Provenance", "Provenance Watcher", "pol"];

    // sq.version next to the exe and Update.exe one folder up. A hand-copied or local build never touches Velopack
    public static bool InstalledCopy { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "sq.version")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "Update.exe"));

    internal sealed record Problem(string Title, string Detail, bool CanRetry = true);

    private const string HasToUpdate = "Provenance has to be up to date before it can start. ";
    internal static readonly Problem CantCheck = new("Provenance couldn't check for updates", HasToUpdate + "Check your internet connection and try again.");
    internal static readonly Problem CantDownload = new("Provenance couldn't download the update", HasToUpdate + "Check your internet connection and try again.");
    internal static readonly Problem CantInstall = new("Provenance couldn't install the update", HasToUpdate + "Restart your PC and try again.");
    internal static readonly Problem CantStartCheck = new("Provenance couldn't check for updates",
        HasToUpdate + "Try again, and if this keeps happening, reinstall Provenance.");
    internal static readonly Problem CantVerify = new("Provenance couldn't verify the update",
        HasToUpdate + "The update didn't pass its security check, so it wasn't installed. Try again later.");

    // the locator's the one the install gets handed to, so the check before it looks at the same file
    internal sealed record Updater(UpdateManager Updates, string Root, ChannelSource Source, IVelopackLocator Locator);

    internal sealed class UpdateProblem(Problem problem) : Exception(problem.Title)
    {
        public Problem Problem { get; } = problem;
    }

    private enum Step { Start, Close, HandedOver }

    // the start found an update it couldn't put in yet
    private static volatile bool _waiting;
    private static long _lastQuiet = long.MinValue;

    private static long LastQuiet { get => Interlocked.Read(ref _lastQuiet); set => Interlocked.Exchange(ref _lastQuiet, value); }

    // true when this copy should close: an update's installing, or it couldn't be brought up to date
    public static Task<bool> RunAsync(LaunchArgs args, bool noAutoLogin)
    {
        if (InstalledCopy) return RunAsync(args, noAutoLogin, Open, WindowManager.ShowUpdate);
        Log.Information("Not an installed copy, so no update check");
        return Task.FromResult(false);
    }

    internal static async Task<bool> RunAsync(LaunchArgs args, bool noAutoLogin, Func<Updater> open, Func<UpdateWindow> newWindow)
    {
        UpdateWindow? window = null;
        using var quit = new CancellationTokenSource();
        void Quit(object? sender, EventArgs e) => quit.Cancel();
        UpdateWindow Show()
        {
            if (window is null)
            {
                window = newWindow();
                window.Closed += Quit;
            }
            window.ShowChecking();
            return window;
        }
        bool handedOver = false, afterUpdate = args.AfterUpdate;
        try
        {
            while (true)
            {
                Problem problem;
                try
                {
                    var step = await Attempt(open, afterUpdate, args.ForRestart(noAutoLogin), Show, quit.Token);
                    handedOver = step == Step.HandedOver;
                    return step != Step.Start;
                }
                catch (UpdateProblem p) { problem = p.Problem; }
                catch (OperationCanceledException) when (quit.IsCancellationRequested) { problem = CantCheck; }
                // a try again goes the normal way, which installs a package that didn't go in again
                afterUpdate = false;
                if (quit.IsCancellationRequested || window is null || !await window.AskTryAgain(problem.Title, problem.Detail))
                {
                    Log.Information("Provenance isn't starting, it couldn't be brought up to date");
                    return true;
                }
                Log.Information("Trying the update again");
            }
        }
        finally
        {
            // a handed-over window stays up until the shutdown
            if (window is not null)
            {
                window.Closed -= Quit;
                if (!handedOver)
                {
                    try { window.Close(); }
                    catch (Exception ex) { Log.Debug(ex, "The update window would not close"); }
                }
            }
        }
    }

    private static async Task<Step> Attempt(Func<Updater> open, bool afterUpdate, string[] restartArgs,
                                            Func<UpdateWindow> show, CancellationToken quit)
    {
        Updater opened;
        try
        {
            opened = open();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check could not start");
            show();
            throw new UpdateProblem(CantStartCheck);
        }
        var (updates, root, source, locator) = opened;
        if (!updates.IsInstalled)
        {
            Log.Information("Not an installed copy, so no update check");
            return Step.Start;
        }
        // a copy that starts while Update.exe swaps the files would only get killed by it
        if (FindRunning(root, "Update") is { } updater)
        {
            Log.Information("{Updater} is installing an update and starts Provenance again afterwards, so this copy closes", updater);
            return Step.Close;
        }
        if (FindRunning(root, InUseNames) is { } user)
        {
            Log.Information("{Process} is running from the install folder, so an update waits for a start without it", user);
            return Step.Start;
        }

        var window = show();
        // the package still waiting after the restart means Update.exe couldn't swap the files (something held one open)
        if (afterUpdate && updates.UpdatePendingRestart?.Version is { } stuck)
        {
            Log.Warning("Provenance {Version} did not install, this is still {Current}", stuck, updates.CurrentVersion);
            throw new UpdateProblem(CantInstall);
        }
        if (await Check(updates, quit, CheckTimeout) is not { } update) return Step.Start;

        var target = update.TargetFullRelease;
        await DownloadChecked(updates, update, window, quit);
        // again, a copy started during the download would get killed. The package stays, so the next start installs it
        if (FindRunning(root, InUseNames) is { } late)
        {
            Log.Information("Not installing {Version} yet, {Process} started from the install folder meanwhile", target.Version, late);
            _waiting = true;
            return Step.Start;
        }

        await CheckBeforeInstall(source, locator, target, quit);
        QueueInstall(window, locator, target, restartArgs);
        return Step.HandedOver;
    }

    private static async Task DownloadChecked(UpdateManager updates, UpdateInfo update, UpdateWindow window, CancellationToken quit)
    {
        var target = update.TargetFullRelease;
        try { await Download(updates, update, window, quit); }
        catch (OperationCanceledException) when (quit.IsCancellationRequested) { throw; }
        catch (ReleaseRefusedException ex)
        {
            Log.Warning("Provenance {Version} was refused: {Reason}", target.Version, ex.Message);
            throw new UpdateProblem(CantVerify);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The download of Provenance {Version} failed", target.Version);
            throw new UpdateProblem(CantDownload);
        }
    }

    internal static readonly Problem NothingToInstall = new("There's no update to install right now",
        "Provenance is up to date, so nothing has been closed.", CanRetry: false);

    // after a try again that closed some copies earlier, nothing's been closed is no longer true
    internal static Problem NothingToInstallAfter(int closed) =>
        closed == 0 ? NothingToInstall : new("There's no update to install right now", "Provenance is up to date, so there's nothing to install.", CanRetry: false);

    // what the other copies did when asked: how many there were, and how many were still running after the wait
    internal sealed record Others(int Asked, int Left)
    {
        public static readonly Others None = new(0, 0);
    }

    // closedBefore is what earlier tries of the same Restart Now closed
    internal static Problem CantClose(Others others, int closedBefore = 0) => closedBefore + others.Asked - others.Left > 0
        ? new("Provenance couldn't close another copy",
              "Some copies of Provenance closed, but another is still running, so the update wasn't installed. Close it yourself, then try again.")
        : new("Provenance couldn't close another copy",
              "Another copy of Provenance is still running, so nothing has been closed or updated. Close it yourself, then try again.");

    private sealed class Tally { public int Closed; }

    // Restart Now, after a yes: the update is downloaded and verified first, then the other copies are closed. A problem
    // on the way closes nothing, and true means this copy should exit.
    internal static async Task<bool> PrepareRestartAsync(Func<Updater> open, Func<UpdateWindow> newWindow,
                                                         Func<CancellationToken, Task<Others>> closeOthers)
    {
        using var quit = new CancellationTokenSource();
        void Quit(object? sender, EventArgs e) => quit.Cancel();
        var window = newWindow();
        window.Closed += Quit;
        window.CancelRequested += Quit;
        var tally = new Tally();
        bool going = false;
        try
        {
            while (true)
            {
                Problem problem;
                try
                {
                    window.ShowChecking();
                    if (!await Verified(open, window, closeOthers, tally, quit.Token)) return false;
                    going = true;
                    return true;
                }
                catch (UpdateProblem p) { problem = p.Problem; }
                catch (OperationCanceledException) when (quit.IsCancellationRequested)
                {
                    Log.Information("Restart Now was cancelled, {Closed} other copies had already closed", tally.Closed);
                    return false;
                }
                if (quit.IsCancellationRequested || !await window.AskTryAgain(problem.Title, problem.Detail, problem.CanRetry))
                {
                    if (tally.Closed == 0) Log.Information("Restart Now stopped, nothing was closed");
                    else Log.Information("Restart Now stopped, {Closed} other copies had already closed", tally.Closed);
                    return false;
                }
            }
        }
        finally
        {
            window.Closed -= Quit;
            window.CancelRequested -= Quit;
            // once it's going the window stays up until the exit
            if (!going)
            {
                try { window.Close(); }
                catch (Exception ex) { Log.Debug(ex, "The update window would not close"); }
            }
        }
    }

    private static async Task<bool> Verified(Func<Updater> open, UpdateWindow window, Func<CancellationToken, Task<Others>> closeOthers,
                                             Tally tally, CancellationToken quit)
    {
        Updater opened;
        try { opened = open(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check could not start");
            throw new UpdateProblem(CantStartCheck);
        }
        var (updates, _, source, locator) = opened;
        if (!updates.IsInstalled) return false;
        if (await Check(updates, quit, CheckTimeout) is not { } update)
        {
            Log.Information("There's no update to restart for");
            throw new UpdateProblem(NothingToInstallAfter(tally.Closed));
        }
        await DownloadChecked(updates, update, window, quit);
        await CheckBeforeInstall(source, locator, update.TargetFullRelease, quit);
        window.ShowWaitingToClose();
        var others = await closeOthers(quit);
        tally.Closed += Math.Max(0, others.Asked - others.Left);
        // the window being closed or cancelled during the wait stops it here, with nothing queued
        quit.ThrowIfCancellationRequested();
        if (others.Left > 0) throw new UpdateProblem(CantClose(others, tally.Closed - (others.Asked - others.Left)));
        QueueInstall(window, locator, update.TargetFullRelease, LaunchArgs.ForRestartNow());
        return true;
    }

    // true to go ahead and exit
    internal static Task<bool> PrepareRestartAsync() =>
        InstalledCopy ? PrepareRestartAsync(Open, WindowManager.ShowUpdate, ct => CloseOtherCopiesAsync(ct: ct)) : Task.FromResult(false);

    // Velopack skips the download, and so the check in it, for a package that's already there. This is the file
    // UpdateExe.Apply passes as --package, and one that isn't there is refused too or Update.exe would pick its own
    internal static async Task CheckBeforeInstall(ChannelSource source, IVelopackLocator locator, VelopackAsset target, CancellationToken quit)
    {
        var package = locator.PackagesDir is { } dir ? Path.Combine(dir, target.FileName) : null;
        try { await source.RecheckAsync(target, package, quit); }
        catch (OperationCanceledException) when (quit.IsCancellationRequested) { throw; }
        catch (ReleaseRefusedException ex)
        {
            Log.Warning("Provenance {Version} was refused before the install: {Reason}", target.Version, ex.Message);
            throw new UpdateProblem(CantVerify);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The statement for Provenance {Version} didn't download", target.Version);
            throw new UpdateProblem(CantDownload);
        }
    }

    private static UpdateManager Manager() =>
        new(new ChannelSource(UpdateServer, VelopackLocator.Current.CurrentlyInstalledVersion, feedRead: NoteFeed));

    private static Updater Open()
    {
        var locator = VelopackLocator.Current;
        var source = new ChannelSource(UpdateServer, locator.CurrentlyInstalledVersion, feedRead: NoteFeed);
        return new Updater(new UpdateManager(source, null, locator),
                           locator.RootAppDir ?? throw new InvalidOperationException("Velopack found no install folder."), source, locator);
    }

    // null when it's up to date. Quitting throws OperationCanceledException, anything else that stops it an UpdateProblem
    internal static async Task<UpdateInfo?> Check(UpdateManager updates, CancellationToken quit, TimeSpan timeout)
    {
        Log.Information("Checking for updates (version {Version})", updates.CurrentVersion);
        LastQuiet = Environment.TickCount64;
        var check = Task.Run(() => updates.CheckForUpdatesAsync());
        if (await Task.WhenAny(check, Task.Delay(timeout, quit)) != check)
        {
            _ = check.ContinueWith(t => Log.Debug(t.Exception, "The abandoned update check failed"),
                                   TaskContinuationOptions.OnlyOnFaulted);
            quit.ThrowIfCancellationRequested();
            Log.Warning("Update check took over {Seconds}s", timeout.TotalSeconds);
            throw new UpdateProblem(CantCheck);
        }
        try
        {
            var update = await check;
            if (update is null) Log.Information("Provenance is up to date");
            return update;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed");
            throw new UpdateProblem(CantCheck);
        }
    }

    // for a copy that's running anyway (back from the tray, or started next to the game): true when there's an update
    // waiting for the next start. One that fails says nothing, the next start's own check is the one that holds.
    // A copy that isn't installed has no update to wait for, but still reads the feed for the title's tag
    internal static async Task<bool> UpdateWaitingAsync(Func<UpdateManager>? manager = null, TimeSpan? timeout = null)
    {
        if (_waiting) return true;
        bool installed = manager is not null || InstalledCopy;
        long now = Environment.TickCount64;
        if (LastQuiet != long.MinValue && now - LastQuiet < QuietGap.TotalMilliseconds) return false;
        LastQuiet = now;
        try
        {
            if (!installed)
            {
                await ReadFeedAsync(timeout ?? CheckTimeout);
                return false;
            }
            var updates = (manager ?? Manager)();
            if (!updates.IsInstalled) return false;
            var check = Task.Run(() => updates.CheckForUpdatesAsync());
            if (await Task.WhenAny(check, Task.Delay(timeout ?? CheckTimeout)) != check)
            {
                _ = check.ContinueWith(t => Log.Debug(t.Exception, "The abandoned quiet update check failed"),
                                       TaskContinuationOptions.OnlyOnFaulted);
                Log.Information("The quiet update check took too long, leaving it for the next start");
                return false;
            }
            if (await check is { } update)
            {
                Log.Information("Provenance {Version} is waiting for the next start", update.TargetFullRelease.Version);
                _waiting = true;
            }
            return _waiting;
        }
        catch (Exception ex)
        {
            Log.Information(ex, "The quiet update check failed, leaving it for the next start");
            return false;
        }
    }

    // swapped in tests, so a copy that isn't installed never reads the real server from one
    internal static Func<ChannelSource>? HandCopiedSource { get; set; }

    private static async Task ReadFeedAsync(TimeSpan timeout)
    {
        var source = HandCopiedSource?.Invoke() ?? new ChannelSource(UpdateServer, VersionTag.Own(), feedRead: NoteFeed);
        var read = Task.Run(() => source.GetReleaseFeed(new VelopackLog(), null, ""));
        if (await Task.WhenAny(read, Task.Delay(timeout)) != read)
        {
            _ = read.ContinueWith(t => Log.Debug(t.Exception, "The abandoned feed read failed"), TaskContinuationOptions.OnlyOnFaulted);
            Log.Information("Reading the update feed took too long, so the title has no tag");
            return;
        }
        await read;
    }

    // what the update server's feed says about this copy, the tag in the main window's title. Null until it's been
    // read, and nothing else sets it
    internal static string? Tag => Volatile.Read(ref _tag);

    private static string? _tag;

    private static void SetTag(string? tag) => Volatile.Write(ref _tag, tag);

    internal static event Action? TagChanged;

    // swapped in tests
    internal static Func<SemanticVersion?> OwnVersion { get; set; } = VersionTag.Own;

    internal static void NoteFeed(IReadOnlyList<SemanticVersion> feed)
    {
        // a feed with nothing in it says nothing
        if (feed.Count == 0 || OwnVersion() is not { } own) return;
        var tag = VersionTag.For(own, feed);
        if (tag == Tag) return;
        SetTag(tag);
        TagChanged?.Invoke();
    }

    // the exit's been turned down (or a game's in the way), so the install isn't going and its window shouldn't sit there
    internal static void DropHandOver()
    {
        Interlocked.Exchange(ref _handOver, null);
        if (Interlocked.Exchange(ref _installWindow, null) is not { } window) return;
        try { window.Dispatcher.Invoke(window.Close); }
        catch (Exception ex) { Log.Debug(ex, "The update window would not close"); }
    }

    internal const string HeldBackText =
        "FINAL FANTASY XI didn't close, so the update wasn't installed. It'll be installed the next time Provenance starts with the game closed.";

    internal static void TellIfHeldBack(LaunchArgs args, NoticeBanner notice)
    {
        if (args.UpdateHeld) notice.Show(HeldBackText, "InformationOutline", stays: true);
    }

    // what to start after the exit, if anything. A Restart Now that a game's held up starts Provenance again with no
    // hand-over, and a failed hand-over starts it with the switches it had
    internal static string[]? AfterExit(bool restartOnExit, bool restartForUpdate, Func<bool>? gamesClear = null)
    {
        if (restartForUpdate && !(gamesClear ?? (() => EndGamesForHandOver()))())
        {
            DropHandOver();
            return [LaunchArgs.NoAutoLoginSwitch, LaunchArgs.UpdateHeldSwitch];
        }
        if (restartOnExit) return [];
        return HandOverAfterExit(out var restartArgs) ? null : restartArgs;
    }

    internal static void ShowWaiting(UpdateBanner banner) => banner.Show();

    internal static async Task TellIfUpdateWaiting(UpdateBanner banner)
    {
        if (await UpdateWaitingAsync()) ShowWaiting(banner);
    }

    internal static void ForgetForTest()
    {
        _waiting = false;
        LastQuiet = long.MinValue;
        SetTag(null);
        HandCopiedSource = null;
        OwnVersion = VersionTag.Own;
        Apply = ApplyForReal;
        Volatile.Write(ref _installWindow, null);
    }

    // Restart Now: the other copies are asked to close the way the uninstaller asks, then given time to log out. The
    // request is a plain close, there's no asking a copy whether it can first.
    internal static Task<Others> CloseOtherCopiesAsync(CancellationToken ct = default) =>
        InstallRoot() is { } root ? CloseOthers(root, CountRunning, CloseRequest.AskOthers, RestartWait, ct) : Task.FromResult(Others.None);

    internal static async Task<Others> CloseOthers(string root, Func<string, int> count, Action ask, TimeSpan limit, CancellationToken ct,
                                                   TimeSpan? uncountedWait = null)
    {
        int asked = count(root);
        // always asked, a copy that can't be looked into still gets the request
        ask();
        try
        {
            if (asked == 0)
            {
                await Task.Delay(uncountedWait ?? UncountedWait, ct);
                return Others.None;
            }
            long until = Environment.TickCount64 + (long)limit.TotalMilliseconds;
            while (true)
            {
                int left = count(root);
                if (left == 0) return new Others(asked, 0);
                if (Environment.TickCount64 >= until)
                {
                    Log.Warning("{Left} of {Asked} other copies of Provenance didn't close when asked, so the update isn't installing", left, asked);
                    return new Others(asked, left);
                }
                await Task.Delay(100, ct);
            }
        }
        // what closed so far is still counted, the caller sees the cancel
        catch (OperationCanceledException) { return new Others(asked, asked == 0 ? 0 : count(root)); }
    }

    private static readonly TimeSpan UncountedWait = TimeSpan.FromSeconds(2);

    private static int CountRunning(string root) => Running(root, "Provenance").Count();

    // What EndGames does to a process, swapped in tests
    internal sealed record GameOps(Func<ProcessStamp, bool> IsRunning, Func<ProcessStamp, int> AskToClose, Func<ProcessStamp, bool> EndHard)
    {
        public static readonly GameOps Real = new(s => s.IsRunning(), s => GameWindow.AskToClose(s.Pid),
                                                  s => s.EndIfStill(0, afterMs: HardEndMs) == ProcessStamp.Ending.Ended);
    }

    // Restart Now, after the session's logged out: every pol.exe from this install is asked to close together, and
    // what's left after one grace is ended. A pid that's been reused gets neither, and a second look follows for a late one.
    internal static (int Gone, int Left) EndGamesCounted(string root, Func<string, IEnumerable<ProcessStamp>>? find = null, GameOps? ops = null,
                                                         int graceMs = GraceMs, int rescanMs = RescanMs, Func<string, int>? unreadable = null)
    {
        find ??= FindGames;
        ops ??= GameOps.Real;
        var seen = new List<ProcessStamp>();
        var ours = new List<ProcessStamp>();
        void Pass()
        {
            var mine = find(root).Where(x => !seen.Contains(x)).ToList();
            seen.AddRange(mine);
            mine = mine.Where(ops.IsRunning).ToList();
            ours.AddRange(mine);
            foreach (var stamp in mine)
                if (ops.IsRunning(stamp)) ops.AskToClose(stamp);
            var until = Environment.TickCount64 + graceMs;
            while (mine.Any(ops.IsRunning) && Environment.TickCount64 < until) Thread.Sleep(50);
            foreach (var stamp in mine.Where(ops.IsRunning).ToList())
            {
                if (ops.EndHard(stamp)) Log.Information("Ended pol.exe ({Pid}), it hadn't closed", stamp.Pid);
                else Log.Warning("pol.exe ({Pid}) wouldn't end", stamp.Pid);
            }
        }
        Pass();
        if (seen.Count > 0)
        {
            Thread.Sleep(rescanMs);
            Pass();
        }
        // a game whose start time can't be read can't be closed, so it's still there
        int unread = (unreadable ?? (r => UnreadableGames(r)))(root);
        return (ours.Count(s => !ops.IsRunning(s)), ours.Count(ops.IsRunning) + unread);
    }

    // how long a game gets to close by itself, the wait before the second look, and what a hard end can take
    internal const int GraceMs = 5000, RescanMs = 2000, HardEndMs = 2000;

    internal static int EndGames(string root, Func<string, IEnumerable<ProcessStamp>>? find = null, GameOps? ops = null,
                                 int graceMs = GraceMs, int rescanMs = RescanMs) =>
        EndGamesCounted(root, find, ops, graceMs, rescanMs).Gone;

    private static IEnumerable<ProcessStamp> FindGames(string root) =>
        Running(root, "pol").Where(p => p.Stamp is not null).Select(p => p.Stamp!.Value);

    // ones of ours with a start time that can't be read, counting only those still there
    internal static int UnreadableGames(string root, Func<string, IEnumerable<(string Name, int Pid, ProcessStamp? Stamp)>>? look = null,
                                        Func<int, bool>? exists = null)
    {
        look ??= r => Running(r, "pol");
        exists ??= ProcessStamp.PidRunning;
        int count = 0;
        foreach (var (_, pid, stamp) in look(root))
        {
            if (stamp is not null || !exists(pid)) continue;
            Log.Warning("pol.exe ({Pid}) can't be closed for the update, its start time can't be read", pid);
            count++;
        }
        return count;
    }

    // after the exit's ended the session: the games go, and if one's still there the install isn't handed over (a pol.exe
    // holding pol_hook.dll blocks the swap) and goes in at the next start. True if it's clear to hand over
    internal static bool EndGamesForHandOver(Func<(int Gone, int Left)>? endGames = null, Func<int>? countGames = null, TimeSpan? limit = null,
                                             TimeSpan? scanLimit = null)
    {
        endGames ??= () => InstallRoot() is { } root ? EndGamesCounted(root) : (0, 0);
        countGames ??= () => InstallRoot() is { } root ? Running(root, "pol").Count() : 0;
        var scan = scanLimit ?? ScanLimit;
        // each step is bounded, a scan stuck on a pol.exe that's loading can't hang the exit. Not finishing holds the install
        int found = 1;
        var counting = Bounded(countGames, scan, out var counted, "Counting the games");
        if (counting == Ran.TimedOut) return Hold("Couldn't count the games in time");
        if (counting == Ran.Done) found = counted;
        var wait = limit ?? GamesLimit(found);
        if (Bounded(endGames, wait, out var result, "Closing the games") != Ran.Done) return Hold("The games weren't closed in time");
        if (result.Left == 0) return true;
        return Hold($"{result.Left} game(s) from this install are still running");
    }

    private static bool Hold(string why)
    {
        Log.Warning("{Why}, so the update isn't being installed now and goes in at the next start", why);
        DropHandOver();
        return false;
    }

    private enum Ran { Done, TimedOut, Failed }

    private static Ran Bounded<T>(Func<T> work, TimeSpan limit, out T result, string what)
    {
        result = default!;
        var task = Task.Run(work);
        try
        {
            if (!task.Wait(limit))
            {
                Log.Warning("{What} took over {Seconds}s", what, limit.TotalSeconds);
                return Ran.TimedOut;
            }
            result = task.Result;
            return Ran.Done;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{What} failed", what);
            return Ran.Failed;
        }
    }

    private static readonly TimeSpan ScanLimit = TimeSpan.FromSeconds(5);

    // the worst a close takes is a grace and a hard end for each game, twice over with the second look, and a few seconds spare
    internal static TimeSpan GamesLimit(int games) =>
        TimeSpan.FromSeconds(Math.Min(60, (2 * (GraceMs + Math.Max(1, games) * HardEndMs) + RescanMs) / 1000.0 + 3));

    private static string? InstallRoot() => InstalledCopy ? VelopackLocator.Current.RootAppDir : null;

    private static async Task Download(UpdateManager updates, UpdateInfo update, UpdateWindow window, CancellationToken skip)
    {
        var target = update.TargetFullRelease;
        Log.Information("Downloading Provenance {Version}", target.Version);
        window.ShowDownload(target.Version.ToString());
        // the progress only moves every 2-3%, so a slow line needs a long stall time
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(skip);
        stall.CancelAfter(StallTimeout);
        await Task.Run(() => updates.DownloadUpdatesAsync(update, percent =>
        {
            stall.CancelAfter(StallTimeout);
            window.Dispatcher.BeginInvoke(() =>
            {
                try { window.ShowProgress(percent); }
                catch (Exception ex) { Log.Debug(ex, "The update window could not show the progress"); }
            });
        }, stall.Token));
    }

    private static UpdateWindow? _installWindow;

    private static void QueueInstall(UpdateWindow window, IVelopackLocator locator, VelopackAsset target, string[] restartArgs)
    {
        window.ShowInstalling();
        Volatile.Write(ref _installWindow, window);
        Log.Information("Handing over to Update.exe to install {Version}", target.Version);
        QueueHandOver(() => Apply(locator, target, restartArgs), restartArgs);
    }

    // what's handed to Update.exe, swapped in tests
    internal static Action<IVelopackLocator, VelopackAsset, string[]> Apply { get; set; } = ApplyForReal;

    private static void ApplyForReal(IVelopackLocator locator, VelopackAsset target, string[] restartArgs) =>
        UpdateExe.Apply(locator, target, silent: false, waitPid: 0, restart: true, restartArgs);

    private sealed record HandOver(Action Start, string[] RestartArgs);

    private static HandOver? _handOver;

    internal static void QueueHandOver(Action start, string[] restartArgs) => Volatile.Write(ref _handOver, new HandOver(start, restartArgs));

    // Update.exe starts at the end of the exit with no wait (told to wait on this pid it gets "Access is denied" on the
    // elevated process). False if it won't start
    internal static bool HandOverAfterExit(out string[] restartArgs)
    {
        restartArgs = [];
        if (Interlocked.Exchange(ref _handOver, null) is not { } queued) return true;
        Volatile.Write(ref _installWindow, null);
        restartArgs = queued.RestartArgs;
        try
        {
            queued.Start();
            Log.Information("Update.exe started");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not start Update.exe");
            return false;
        }
    }

    // the exe path for Provenance, the watcher and Update.exe, the loaded modules for pol.exe (it lives in the
    // viewer folder, the hook doesn't)
    private static string? FindRunning(string root, params string[] names) =>
        Running(root, names).Cast<(string Name, int Pid, ProcessStamp? Stamp)?>().FirstOrDefault() is { } found ? $"{found.Name}.exe ({found.Pid})" : null;

    private static IEnumerable<(string Name, int Pid, ProcessStamp? Stamp)> Running(string root, params string[] names)
    {
        bool Inside(string? path) => path is not null && PathInside.IsUnder(root, path);
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (p.Id == Environment.ProcessId) continue;
                    bool ours;
                    try
                    {
                        ours = name == "pol"
                            ? p.Modules.Cast<ProcessModule>().Any(m => Inside(m.FileName))
                            : Inside(ProcessTree.ImagePath(p.Id));
                    }
                    catch { continue; }   // one that can't be looked into isn't one of this install's
                    if (ours) yield return (name, p.Id, ProcessStamp.Of(p.Id));
                }
            }
        }
    }
}
