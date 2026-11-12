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

    // Update.exe kills everything running from the install folder before it swaps the files, and a pol.exe holding
    // pol_hook.dll stops the swap anyway
    private static readonly string[] InUseNames = ["Provenance", "Provenance Watcher", "pol"];

    // sq.version next to the exe and Update.exe one folder up. A hand-copied or local build never touches Velopack
    public static bool InstalledCopy { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "sq.version")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "Update.exe"));

    internal sealed record Problem(string Title, string Detail);

    private const string HasToUpdate = "Provenance has to be up to date before it can start. ";
    internal static readonly Problem CantCheck = new("Provenance couldn't check for updates", HasToUpdate + "Check your internet connection and try again.");
    internal static readonly Problem CantDownload = new("Provenance couldn't download the update", HasToUpdate + "Check your internet connection and try again.");
    internal static readonly Problem CantInstall = new("Provenance couldn't install the update", HasToUpdate + "Restart your PC and try again.");
    internal static readonly Problem CantStartCheck = new("Provenance couldn't check for updates",
        HasToUpdate + "Try again, and if this keeps happening, reinstall Provenance.");

    internal sealed class UpdateProblem(Problem problem) : Exception(problem.Title)
    {
        public Problem Problem { get; } = problem;
    }

    private enum Step { Start, Close, HandedOver }

    // the start found an update it couldn't put in yet, or something running that it has to wait for
    private static bool _waiting, _checkOnceUp;
    private static long _lastQuiet = long.MinValue;

    // true when this copy should close: an update's installing, or it couldn't be brought up to date
    public static Task<bool> RunAsync(LaunchArgs args, bool noAutoLogin)
    {
        if (InstalledCopy) return RunAsync(args, noAutoLogin, Open, WindowManager.ShowUpdate);
        Log.Information("Not an installed copy, so no update check");
        return Task.FromResult(false);
    }

    internal static async Task<bool> RunAsync(LaunchArgs args, bool noAutoLogin, Func<(UpdateManager Updates, string Root)> open,
                                              Func<UpdateWindow> newWindow)
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

    private static async Task<Step> Attempt(Func<(UpdateManager Updates, string Root)> open, bool afterUpdate, string[] restartArgs,
                                            Func<UpdateWindow> show, CancellationToken quit)
    {
        UpdateManager updates;
        string root;
        try
        {
            (updates, root) = open();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check could not start");
            show();
            throw new UpdateProblem(CantStartCheck);
        }
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
            _checkOnceUp = true;
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
        try { await Download(updates, update, window, quit); }
        catch (OperationCanceledException) when (quit.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "The download of Provenance {Version} failed", target.Version);
            throw new UpdateProblem(CantDownload);
        }
        // again, a copy started during the download would get killed. The package stays, so the next start installs it
        if (FindRunning(root, InUseNames) is { } late)
        {
            Log.Information("Not installing {Version} yet, {Process} started from the install folder meanwhile", target.Version, late);
            _waiting = true;
            return Step.Start;
        }

        QueueInstall(window, target, restartArgs);
        return Step.HandedOver;
    }

    private static UpdateManager Manager() =>
        new(new ChannelSource(UpdateServer, VelopackLocator.Current.CurrentlyInstalledVersion));

    private static (UpdateManager, string) Open() =>
        (Manager(), VelopackLocator.Current.RootAppDir ?? throw new InvalidOperationException("Velopack found no install folder."));

    // null when it's up to date. Quitting throws OperationCanceledException, anything else that stops it an UpdateProblem
    internal static async Task<UpdateInfo?> Check(UpdateManager updates, CancellationToken quit, TimeSpan timeout)
    {
        Log.Information("Checking for updates (version {Version})", updates.CurrentVersion);
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
    // waiting for the next start. One that fails says nothing, the next start's own check is the one that holds
    internal static async Task<bool> UpdateWaitingAsync(Func<UpdateManager>? manager = null, TimeSpan? timeout = null)
    {
        if (_waiting) return true;
        if (manager is null && !InstalledCopy) return false;
        long now = Environment.TickCount64;
        if (_lastQuiet != long.MinValue && now - _lastQuiet < QuietGap.TotalMilliseconds) return false;
        _lastQuiet = now;
        try
        {
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

    internal static void ShowWaiting(NoticeBanner notice) => notice.Show(WaitingText, "Update", stays: true);

    internal static async Task TellIfUpdateWaiting(NoticeBanner notice)
    {
        if (!_checkOnceUp && !_waiting) return;
        if (await UpdateWaitingAsync()) ShowWaiting(notice);
    }

    internal static void ForgetForTest()
    {
        _waiting = _checkOnceUp = false;
        _lastQuiet = long.MinValue;
    }

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

    private static void QueueInstall(UpdateWindow window, VelopackAsset target, string[] restartArgs)
    {
        window.ShowInstalling();
        Log.Information("Handing over to Update.exe to install {Version}", target.Version);
        QueueHandOver(() => UpdateExe.Apply(VelopackLocator.Current, target, silent: false, waitPid: 0, restart: true, restartArgs), restartArgs);
    }

    private sealed record HandOver(Action Start, string[] RestartArgs);

    private static HandOver? _handOver;

    internal static void QueueHandOver(Action start, string[] restartArgs) => _handOver = new HandOver(start, restartArgs);

    // Update.exe starts at the end of the exit with no wait (told to wait on this pid it gets "Access is denied" on the
    // elevated process). False if it won't start
    internal static bool HandOverAfterExit(out string[] restartArgs)
    {
        restartArgs = [];
        if (Interlocked.Exchange(ref _handOver, null) is not { } queued) return true;
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
    private static string? FindRunning(string root, params string[] names)
    {
        bool Inside(string? path) => path is not null && PathInside.IsUnder(root, path);
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (p.Id == Environment.ProcessId) continue;
                    try
                    {
                        bool ours = name == "pol"
                            ? p.Modules.Cast<ProcessModule>().Any(m => Inside(m.FileName))
                            : Inside(ProcessTree.ImagePath(p.Id));
                        if (ours) return $"{name}.exe ({p.Id})";
                    }
                    catch { }   // one that can't be looked into isn't one of this install's
                }
            }
        }
        return null;
    }
}
