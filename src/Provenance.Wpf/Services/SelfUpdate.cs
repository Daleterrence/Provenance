using System.Diagnostics;
using System.Globalization;
using System.IO;
using Provenance.Common.Security;
using Provenance.Common;
using Provenance.Common.Game;
using Provenance.Wpf.Views.Install;
using Serilog;
using Velopack;
using Velopack.Locators;

namespace Provenance.Wpf.Services;

// Provenance's own updates, from updates.provenancexi.app, checked once at start-up before anything else runs
public static class SelfUpdate
{
    public const string UpdateServer = "https://updates.provenancexi.app/";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FailedApplyPause = TimeSpan.FromDays(1);

    // Update.exe kills everything running from the install folder before it swaps the files, and a pol.exe holding
    // pol_hook.dll stops the swap anyway
    private static readonly string[] InUseNames = ["Provenance", "Provenance Watcher", "pol"];

    // sq.version next to the exe and Update.exe one folder up. A hand-copied or local build never touches Velopack
    public static bool InstalledCopy { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "sq.version")) &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "..", "Update.exe"));

    // true when this copy should just close: Update.exe is installing an update and starts Provenance again itself
    public static async Task<bool> RunAsync(LaunchArgs args, bool noAutoLogin)
    {
        if (!InstalledCopy)
        {
            Log.Information("Not an installed copy, so no update check");
            return false;
        }

        UpdateManager updates;
        string root;
        try
        {
            updates = new UpdateManager(new ChannelSource(UpdateServer, VelopackLocator.Current.CurrentlyInstalledVersion));
            root = VelopackLocator.Current.RootAppDir ?? throw new InvalidOperationException("Velopack found no install folder.");
            if (WhyNoCheck(args, updates, root) is { } why) return why.CloseThisCopy;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check could not start");
            return false;
        }

        UpdateWindow? window = null;
        using var skip = new CancellationTokenSource();
        void Skip(object? sender, EventArgs e) => skip.Cancel();
        bool handedOver = false;
        try
        {
            window = WindowManager.ShowUpdate();
            window.Closed += Skip;
            window.SkipClicked += Skip;

            Log.Information("Checking for updates (version {Version})", updates.CurrentVersion);
            var check = Task.Run(() => updates.CheckForUpdatesAsync());
            if (await Task.WhenAny(check, Task.Delay(CheckTimeout, skip.Token)) != check)
            {
                _ = check.ContinueWith(t => Log.Debug(t.Exception, "The abandoned update check failed"),
                                       TaskContinuationOptions.OnlyOnFaulted);
                if (skip.IsCancellationRequested) Log.Information("Update check skipped");
                else Log.Warning("Update check took over {Seconds}s, carrying on without it", CheckTimeout.TotalSeconds);
                return false;
            }
            var update = await check;
            if (update is null)
            {
                Log.Information("Provenance is up to date");
                return false;
            }

            var target = update.TargetFullRelease;
            if (FailedRecently(target.Version))
            {
                Log.Information("Provenance {Version} failed to install less than a day ago, not trying it again yet", target.Version);
                return false;
            }
            Log.Information("Downloading Provenance {Version}", target.Version);
            window.ShowDownload(target.Version.ToString());
            // the progress only moves every 2-3%, so a slow line needs a long stall time
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(skip.Token);
            stall.CancelAfter(StallTimeout);
            var shown = window;
            await Task.Run(() => updates.DownloadUpdatesAsync(update, percent =>
            {
                stall.CancelAfter(StallTimeout);
                shown.Dispatcher.BeginInvoke(() =>
                {
                    try { shown.ShowProgress(percent); }
                    catch (Exception ex) { Log.Debug(ex, "The update window could not show the progress"); }
                });
            }, stall.Token));

            // again, a copy started during the download would get killed. The package stays, so the next start installs it
            if (skip.IsCancellationRequested)
            {
                Log.Information("Update skipped");
                return false;
            }
            if (FindRunning(root, InUseNames) is { } late)
            {
                Log.Information("Not installing {Version} yet, {Process} started from the install folder meanwhile", target.Version, late);
                return false;
            }

            window.ShowInstalling();
            Log.Information("Handing over to Update.exe to install {Version}", target.Version);
            var restartArgs = args.ForRestart(noAutoLogin);
            QueueHandOver(() => UpdateExe.Apply(VelopackLocator.Current, target, silent: false, waitPid: 0, restart: true, restartArgs), restartArgs);
            handedOver = true;
            return true;
        }
        catch (OperationCanceledException) when (skip.IsCancellationRequested)
        {
            Log.Information("Update skipped");
            return false;
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Update download stalled for {Minutes} minutes, carrying on without it", StallTimeout.TotalMinutes);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Update check failed, carrying on without it");
            return false;
        }
        finally
        {
            // a handed-over window stays up until the shutdown, and skip's gone by then
            if (window is not null)
            {
                window.Closed -= Skip;
                window.SkipClicked -= Skip;
                if (!handedOver)
                {
                    try { window.Close(); }
                    catch (Exception ex) { Log.Debug(ex, "The update window would not close"); }
                }
            }
        }
    }

    private static Action? _handOver;
    private static string[] _handOverArgs = [];

    internal static void QueueHandOver(Action start, string[] restartArgs)
    {
        _handOverArgs = restartArgs;
        _handOver = start;
    }

    // Update.exe starts at the end of the exit with no wait, told to wait on our pid it gets "Access is denied" opening
    // the elevated process. False if it won't start, restartArgs is then what to start the app again with
    internal static bool HandOverAfterExit(out string[] restartArgs)
    {
        restartArgs = _handOverArgs;
        var start = Interlocked.Exchange(ref _handOver, null);
        if (start is null) return true;
        try
        {
            start();
            Log.Information("Update.exe started");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not start Update.exe");
            return false;
        }
    }

    private readonly record struct NoCheck(bool CloseThisCopy);

    // null when the check should go ahead. Logs why when it shouldn't
    private static NoCheck? WhyNoCheck(LaunchArgs args, UpdateManager updates, string root)
    {
        if (!updates.IsInstalled)
        {
            Log.Information("Not an installed copy, so no update check");
            return new NoCheck(false);
        }
        if (args.SkipUpdateCheck)
        {
            if (args.AfterUpdate)
            {
                Log.Information("Update check skipped, this start follows an update");
                NoteApplyResult(updates);
            }
            else Log.Information("Update check skipped ({Arg})", LaunchArgs.SkipUpdateCheckSwitch);
            return new NoCheck(false);
        }
        // a copy that starts while Update.exe swaps the files would only get killed by it
        if (FindRunning(root, "Update") is { } updater)
        {
            Log.Information("{Updater} is installing an update and starts Provenance again afterwards, so this copy closes", updater);
            return new NoCheck(true);
        }
        if (FindRunning(root, InUseNames) is { } user)
        {
            Log.Information("Update check skipped, {Process} is running from the install folder", user);
            return new NoCheck(false);
        }
        return null;
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

    private static string FailedApplyFile =>
        Path.Combine(AppPaths.UserData ?? "Provenance", "failed-update.txt");

    // the package still waiting after the restart means Update.exe couldn't swap the files (something held one open).
    // Without a note every start would try again and fail the same way
    private static void NoteApplyResult(UpdateManager updates)
    {
        try
        {
            if (updates.UpdatePendingRestart is { } stuck)
            {
                Log.Warning("Provenance {Version} did not install, this is still {Current}. It gets another try in a day",
                            stuck.Version, updates.CurrentVersion);
                File.WriteAllText(FailedApplyFile, $"{stuck.Version} {DateTime.UtcNow:O}");
            }
            else File.Delete(FailedApplyFile);
        }
        catch (Exception ex) { Log.Debug(ex, "Could not note how the update went"); }
    }

    private static bool FailedRecently(SemanticVersion version)
    {
        try
        {
            if (!File.Exists(FailedApplyFile)) return false;
            var parts = File.ReadAllText(FailedApplyFile).Trim().Split(' ', 2);
            if (parts.Length != 2 || parts[0] != version.ToString() ||
                !DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                return false;
            // a time in the future (a clock that was ahead) mustn't hold the update back until then
            var ago = DateTime.UtcNow - at.ToUniversalTime();
            return ago >= TimeSpan.Zero && ago < FailedApplyPause;
        }
        catch { return false; }
    }
}
