using System.Diagnostics;
using System.IO.Compression;
using Provenance.Common.Security;
using Provenance.Common.IO;

namespace Provenance.Common.Game.Windower;

public sealed record WindowerInstallResult(string Name, bool Installed, IReadOnlyList<string> Written, IReadOnlyList<string> Errors);

public enum WindowerBranchState { Matches, UpdatePending, NoManifest }

// nothing records which branch a file came from, so the cached manifest is all there is to go on. SwitchPending: it's
// still the old branch's
public sealed record WindowerBranchInfo(string Branch, Version HookFile, Version LauncherFile, Version? HookManifest, Version? LauncherManifest,
                                        bool SwitchPending = false)
{
    public WindowerBranchState State =>
        HookManifest is null ? WindowerBranchState.NoManifest
        : !SwitchPending && HookFile == HookManifest && LauncherFile == LauncherManifest ? WindowerBranchState.Matches
        : WindowerBranchState.UpdatePending;
}

// Windower.exe's start-up updates without Windower.exe. The same cache files in updates\, so either carries on from the
// other, and Windower.exe's replaced when the manifest says but never run
public sealed class WindowerUpdater
{
    public const string MutexName = "Windower.Update.Mutex";

    public static readonly IReadOnlyList<string> RunningNames = ["Windower", "pol", "xiloader"];

    public WindowerUpdater(string windowerDir, string? branch)
    {
        Folder = Path.GetFullPath(windowerDir);
        Branch = branch == "dev" ? "dev" : "stable";
    }

    public string Folder { get; }
    public string Branch { get; }
    public WindowerServers Servers { get; init; } = WindowerServers.Default;
    public Func<bool> OthersRunning { get; init; } = AnyRunning;

    // Windower waits forever, which is no good before a launch
    public TimeSpan MutexWait { get; init; } = TimeSpan.FromSeconds(30);

    // a Windower.exe that took the lock over from one that died keeps it till it exits, so with one open the wait's short
    public Func<bool> WindowerRunning { get; init; } = () => IsRunning("Windower");

    private string TempDir => WindowerUpdateFolder.TempDirOf(Folder);
    private string HookPath => Path.Combine(Folder, "Hook.dll");
    private string LauncherPath => Path.Combine(Folder, "Windower.exe");
    private string SwitchFlag => Path.Combine(Folder, "updates", "branch-switch");

    public static bool AnyRunning() => RunningNames.Any(IsRunning);

    public static bool IsRunning(string name)
    {
        var found = Process.GetProcessesByName(name);
        bool other = found.Any(p => p.Id != Environment.ProcessId);
        foreach (var p in found) p.Dispose();
        return other;
    }

    public async Task<WindowerUpdateResult> UpdateAsync(CancellationToken ct = default)
    {
        using var run = await BeginAsync(ct);
        return await run.FinishAsync();
    }

    // up to and including the core files, with the update lock and the folder's pins still held for FinishAsync
    internal async Task<WindowerUpdateRun> BeginAsync(CancellationToken ct = default)
    {
        var result = new WindowerUpdateResult(Branch);
        result.HookBefore = result.HookAfter = WindowerFiles.FileVersion(HookPath);
        result.LauncherBefore = result.LauncherAfter = WindowerFiles.LauncherVersion(LauncherPath);
        var (hold, timedOut) = await TakeMutexAsync(result.Errors, ct);
        if (hold is null)
        {
            result.LockTimedOut = timedOut;
            result.HasManifest = WindowerManifest.LoadCached(Folder) is not null;
            return new WindowerUpdateRun(result);
        }
        WindowerUpdateFolder? job = null;
        try
        {
            job = Open(result.Errors, ct);
            if (job is null)
            {
                hold.Dispose();
                return new WindowerUpdateRun(result);
            }
            bool skip = OthersRunning();
            if (skip)
            {
                result.Skipped = true;
                result.SkipReason = "PlayOnline, xiloader or Windower is running, so Windower only got its resource files and anything missing.";
            }

            var manifest = WindowerManifest.Empty;
            await job.Step(async () => manifest = await ManifestAsync(job, skip, result));
            await job.Step(() => CoreAsync(job, manifest, skip, result));
            return new WindowerUpdateRun(this, result, hold, job, manifest, skip);
        }
        catch
        {
            job?.Dispose();
            hold.Dispose();
            throw;
        }
    }

    internal bool PluginsPending(WindowerUpdateFolder job, WindowerManifest manifest, bool skip) =>
        new WindowerPluginUpdate(job, Servers, Branch).AnyToFetch(manifest, skip);

    internal async Task RestAsync(WindowerUpdateFolder job, WindowerManifest manifest, bool skip, WindowerUpdateResult result)
    {
        await job.Step(() => ResAsync(job, result));
        await job.Step(() => new WindowerPluginUpdate(job, Servers, Branch).UpdateAllAsync(manifest, skip, result));
        await job.Step(() => new WindowerAddonUpdate(job, Servers, Branch).UpdateAsync(skip, result));
        if (!skip) job.Cleanup();
        result.UnreachableHosts = job.Http.OfflineHosts.ToList();
    }

    // the DLL with its file's date (so normally the 1601 one), then the zip only if the DLL came down
    public async Task<WindowerInstallResult> InstallPluginAsync(string name, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var written = new List<string>();
        var (hold, _) = await TakeMutexAsync(errors, ct);
        using var h = hold;
        using var job = hold is null ? null : Open(errors, ct);
        if (job is null) return new(name, false, written, errors);
        var plugin = WindowerManifest.LoadCached(Folder)?.Find(name);
        if (plugin is null || !WindowerFiles.IsPlainName(plugin.Name))
            return new(name, false, written, [$"{name} isn't in Windower's plugin list."]);
        var plugins = new WindowerPluginUpdate(job, Servers, Branch);
        var dll = plugins.DllPath(plugin.Name);
        var outcome = WindowerFetchOutcome.Unreachable;
        await job.Step(async () => outcome = await plugins.FetchAsync(plugin, dll, install: true, written));
        bool installed = outcome == WindowerFetchOutcome.Downloaded || outcome == WindowerFetchOutcome.NotModified && File.Exists(dll);
        return new(plugin.Name, installed, written, errors);
    }

    public async Task<WindowerInstallResult> InstallAddonAsync(string name, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var written = new List<string>();
        var (hold, _) = await TakeMutexAsync(errors, ct);
        using var h = hold;
        using var job = hold is null ? null : Open(errors, ct);
        if (job is null) return new(name, false, written, errors);
        var zipPath = WindowerAddonTree.ZipPath(Folder);
        var tree = File.Exists(zipPath) ? WindowerAddonTree.Load(WindowerAddonTree.CachePath(Folder)) : null;
        if (tree is null) return new(name, false, written, ["Windower's addons haven't been downloaded yet."]);
        var addon = tree.Find(name);
        if (addon is null) return new(name, false, written, [$"{name} isn't in Windower's addon list."]);
        var addons = new WindowerAddonUpdate(job, Servers, Branch);
        await job.Step(() =>
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var f in addon.Files)
                if (addons.WriteFile(zip, f)) written.Add(f.Path);
            return Task.CompletedTask;
        });
        return new(addon.Name, WindowerAddonTree.IsInstalled(addon, Folder), written, errors);
    }

    // The manifest cache isn't per branch, so the first request after a switch sends the 1601 date rather than risk a
    // 304. The cache stays as a fallback
    public async Task<bool> PrepareBranchSwitchAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();
        var (hold, _) = await TakeMutexAsync(errors, ct);
        using var h = hold;
        using var job = hold is null ? null : Open(errors, ct);
        if (job is null || !job.Ready(SwitchFlag)) return false;
        try
        {
            var temp = job.NewTemp();
            await File.WriteAllTextAsync(temp, Branch, ct);
            job.Move(temp, SwitchFlag, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public WindowerBranchInfo BranchInfo()
    {
        var m = WindowerManifest.LoadCached(Folder);
        return new(Branch, WindowerFiles.FileVersion(HookPath), WindowerFiles.LauncherVersion(LauncherPath), m?.HookVersion, m?.LauncherVersion,
                   File.Exists(SwitchFlag));
    }

    private WindowerHttp NewHttp()
    {
        var v = WindowerFiles.LauncherVersion(LauncherPath);
        return new WindowerHttp { UserAgent = WindowerHttp.UserAgentFor(v) };
    }

    private async Task<(WindowerUpdateMutex? Hold, bool TimedOut)> TakeMutexAsync(List<string> errors, CancellationToken ct)
    {
        if (!Directory.Exists(Folder))
        {
            errors.Add($"The Windower folder {Folder} doesn't exist.");
            return (null, false);
        }
        try
        {
            var wait = WindowerRunning() && MutexWait > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : MutexWait;
            var hold = await WindowerUpdateMutex.AcquireAsync(MutexName, wait, ct);
            if (hold is null)
                errors.Add($"Windower's update lock was held elsewhere for {wait.TotalSeconds:0.#}s, so nothing was changed this time.");
            return (hold, hold is null);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            errors.Add(WindowerUpdateFolder.Reason(e, "Windower's update lock couldn't be taken, so nothing was changed this time. Try again in a moment."));
            return (null, false);
        }
    }

    // Users can write in a Windower folder and this runs elevated, so the root, updates and updates\temp are held for
    // the whole run and refused if any is a link
    private WindowerUpdateFolder? Open(List<string> errors, CancellationToken ct)
    {
        FolderPins? pins = null;
        try { pins = FolderPins.HoldExact(Folder, TempDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (pins is not null) return new WindowerUpdateFolder(Folder, NewHttp(), pins, errors, ct);
        errors.Add("The Windower folder, updates or updates\\temp is a link (or can't be opened), so nothing was changed.");
        return null;
    }

    private async Task<WindowerManifest> ManifestAsync(WindowerUpdateFolder job, bool skip, WindowerUpdateResult result)
    {
        var cache = WindowerManifest.CachePath(Folder);
        var cached = WindowerManifest.Load(cache);
        bool switched = File.Exists(SwitchFlag);
        WindowerManifest? fresh = null;
        // a skipped run still fetches a manifest that isn't there, nothing else says what to install
        if (!skip || cached is null)
        {
            try
            {
                var since = switched ? WindowerHttp.NoFile : WindowerHttp.Modified(cache);
                var (_, temp) = await job.FetchToTemp(Servers.Files, $"{Branch}/manifest.xml", since, "Windower's manifest");
                if (temp is not null)
                {
                    if (TextFiles.LoadXml(temp) is not { } doc) job.Errors.Add("Windower's manifest from the server wasn't readable.");
                    else
                    {
                        fresh = WindowerManifest.Parse(doc);
                        result.ManifestDownloaded = true;
                        if (job.Ready(cache))
                        {
                            job.Swap(temp, cache);
                            if (switched) job.Delete(SwitchFlag);
                        }
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !job.Ct.IsCancellationRequested)
            {
                job.Errors.Add($"Windower's manifest wasn't updated: {e.Message}");
            }
        }
        var manifest = fresh ?? cached;
        if (manifest is null && File.Exists(cache)) job.Delete(cache);
        result.HasManifest = manifest is not null;
        if (manifest is null) job.Errors.Add("There's no Windower manifest yet, so no plugins will load until one downloads.");
        return manifest ?? WindowerManifest.Empty;
    }

    // Windower checks the hook only when it didn't just replace itself (it restarts instead). != on purpose, a lower
    // version on the server is a downgrade
    private async Task CoreAsync(WindowerUpdateFolder job, WindowerManifest manifest, bool skip, WindowerUpdateResult result)
    {
        if (!skip && result.LauncherBefore != manifest.LauncherVersion &&
            await ReplaceAsync(job, $"{Branch}/Windower.exe", LauncherPath, "Windower's own launcher"))
        {
            result.LauncherAfter = WindowerFiles.LauncherVersion(LauncherPath);
            job.Http.UserAgent = WindowerHttp.UserAgentFor(result.LauncherAfter);
        }
        // a missing Hook.dll can't be loaded by anything, so it comes down even while games run
        if ((!skip || !File.Exists(HookPath)) && WindowerFiles.FileVersion(HookPath) != manifest.HookVersion &&
            await ReplaceAsync(job, $"{Branch}/Hook.dll", HookPath, "Hook.dll"))
            result.HookAfter = WindowerFiles.FileVersion(HookPath);
    }

    private async Task<bool> ReplaceAsync(WindowerUpdateFolder job, string url, string target, string what)
    {
        if (!job.Ready(target)) return false;
        string? temp = null;
        try
        {
            (_, temp) = await job.FetchToTemp(Servers.Files, url, null, what, WindowerFiles.LooksLikePe,
                                              $"The download for {what} isn't a program file, so the old one was kept.");
            if (temp is null) return false;
            job.Swap(temp, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            job.Errors.Add($"{what} couldn't be replaced, the old one was kept: {e.Message}");
            return false;
        }
        finally
        {
            if (temp is not null) job.Delete(temp);
        }
    }

    // runs with games up too. The old resources.xml goes first, so a run that stops part way leaves none and the next
    // one checks every file again
    private async Task ResAsync(WindowerUpdateFolder job, WindowerUpdateResult result)
    {
        var resDir = Path.Combine(Folder, "res");
        var cache = Path.Combine(Folder, "updates", "resources.xml");
        if (!job.Pins.Pin(resDir))
        {
            job.Errors.Add("Windower's res folder is a link, so its resource files weren't checked.");
            return;
        }
        var (_, temp) = await job.FetchToTemp(Servers.Res, "manifest.xml", WindowerHttp.Modified(cache), "The resource list");
        if (temp is null) return;
        if (TextFiles.LoadXml(temp) is not { } doc)
        {
            job.Delete(temp);
            job.Errors.Add("The resource list from the server wasn't readable.");
            return;
        }
        var names = WindowerManifest.ParseRes(doc);
        if (names.Count == 0)
        {
            job.Delete(temp);
            job.Errors.Add("Windower's resource list came back empty, so the resource files were left as they are.");
            return;
        }
        job.Backup(cache);
        if (!job.Pins.Intact(resDir))
        {
            job.Delete(temp);
            job.Errors.Add("Windower's res folder turned into a link, so its resource files weren't touched.");
            return;
        }

        // name without its last extension, case and all, so items.lua.bak and Items.lua both go when the list says items
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(resDir))
        {
            if (wanted.Contains(Path.GetFileNameWithoutExtension(file))) continue;
            if (job.Pins.DeleteEntry(file)) result.ResRemoved.Add(Path.GetFileName(file));
            else job.Errors.Add($"{Path.GetFileName(file)} couldn't be removed from res.");
        }

        bool whole = true;
        foreach (var name in names)
        {
            job.Ct.ThrowIfCancellationRequested();
            if (!WindowerFiles.IsPlainName(name))
            {
                job.Errors.Add($"The resource list names a file that isn't allowed ({name}), so it was skipped.");
                continue;
            }
            var target = Path.Combine(resDir, name + ".lua");
            if (!job.Ready(target))
            {
                whole = false;
                continue;
            }
            var (r, t) = await job.FetchToTemp(Servers.Res, $"lua/{name}.lua", WindowerHttp.Modified(target), name + ".lua");
            if (t is null)
            {
                whole &= r.Outcome is WindowerFetchOutcome.NotModified or WindowerFetchOutcome.HttpError;
                continue;
            }
            try
            {
                job.Swap(t, target);
                result.ResUpdated.Add(name + ".lua");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                whole = false;
                job.Delete(t);
                job.Errors.Add($"{name}.lua couldn't be replaced, the old one was kept: {e.Message}");
            }
        }
        // Windower saves the list anyway, but then a file that didn't come down isn't looked at again till it changes
        if (whole && job.Ready(cache)) job.Move(temp, cache);
        else job.Delete(temp);
    }
}
