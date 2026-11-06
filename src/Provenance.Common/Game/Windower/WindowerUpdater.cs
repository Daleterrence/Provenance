using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Provenance.Common.Security;
using Serilog;

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

    private string TempDir => WindowerFiles.TempDir(Folder);
    private string HookPath => Path.Combine(Folder, "Hook.dll");
    private string LauncherPath => Path.Combine(Folder, "Windower.exe");
    private string PluginsDir => Path.Combine(Folder, "plugins");
    private string AddonsDir => Path.Combine(Folder, "addons");
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
        var result = new WindowerUpdateResult(Branch);
        result.HookBefore = result.HookAfter = WindowerFiles.FileVersion(HookPath);
        result.LauncherBefore = result.LauncherAfter = WindowerFiles.LauncherVersion(LauncherPath);
        var (hold, timedOut) = await TakeMutexAsync(result.Errors, ct);
        using var held = hold;
        if (hold is null)
        {
            result.LockTimedOut = timedOut;
            result.HasManifest = WindowerManifest.LoadCached(Folder) is not null;
            return result;
        }
        using var job = Open(result.Errors, ct);
        if (job is null) return result;
        bool skip = OthersRunning();
        if (skip)
        {
            result.Skipped = true;
            result.SkipReason = "PlayOnline, xiloader or Windower is running, so Windower only got its resource files and anything missing.";
        }

        var manifest = WindowerManifest.Empty;
        await job.Step(async () => manifest = await ManifestAsync(job, skip, result));
        await job.Step(() => CoreAsync(job, manifest, skip, result));
        await job.Step(() => ResAsync(job, result));
        await job.Step(() => PluginsAsync(job, manifest, skip, result));
        await job.Step(() => AddonsAsync(job, skip, result));
        if (!skip) job.Cleanup();
        result.UnreachableHosts = job.Http.OfflineHosts.ToList();
        return result;
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
        var dll = Path.Combine(PluginsDir, plugin.Name + ".dll");
        var outcome = WindowerFetchOutcome.Unreachable;
        await job.Step(async () => outcome = await PluginAsync(job, plugin, dll, install: true, written));
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
        await job.Step(() =>
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var f in addon.Files)
                if (WriteAddonFile(job, zip, f)) written.Add(f.Path);
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
            var temp = WindowerFiles.NewTemp(TempDir);
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
        return new WindowerHttp { UserAgent = WindowerHttp.UserAgentFor(v.Major > 0 ? v : WindowerHttp.DefaultLauncher) };
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
                errors.Add($"Windower's update lock was held elsewhere for {wait.TotalSeconds:0.#} s, so nothing was changed this time.");
            return (hold, hold is null);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            errors.Add(Reason(e, "Windower's update lock couldn't be taken, so nothing was changed this time. Try again in a moment."));
            return (null, false);
        }
    }

    // install shows these, so Windows' and .NET's own wording only goes in the log
    private string Reason(Exception e, string ours)
    {
        if (Shown.Is(e)) return e.Message;
        Log.Debug("Windower update: {Line}", $"{ours} ({e.GetType().Name}: {e.Message})");
        return ours;
    }

    private string WriteFailed(Exception e, string what) =>
        Reason(e, $"{what} couldn't be written. Try again, and if it keeps happening, check nothing else is using it and the drive isn't full.");

    // Users can write in a Windower folder and this runs elevated, so the root, updates and updates\temp are held for
    // the whole run and refused if any is a link
    private Job? Open(List<string> errors, CancellationToken ct)
    {
        FolderPins? pins = null;
        try { pins = FolderPins.HoldExact(Folder, TempDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (pins is not null) return new Job(this, pins, errors, ct);
        errors.Add("The Windower folder, updates or updates\\temp is a link (or can't be opened), so nothing was changed.");
        return null;
    }

    private sealed class Job : IDisposable
    {
        private readonly WindowerUpdater _owner;
        private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);
        public readonly WindowerHttp Http;
        public readonly FolderPins Pins;
        public readonly List<string> Errors;
        public readonly CancellationToken Ct;

        public Job(WindowerUpdater owner, FolderPins pins, List<string> errors, CancellationToken ct)
        {
            _owner = owner;
            Http = owner.NewHttp();
            Pins = pins;
            Errors = errors;
            Ct = ct;
        }

        public bool Ready(string target)
        {
            var dir = Path.GetDirectoryName(target)!;
            bool pinned;
            try { pinned = Pins.Pin(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { pinned = false; }
            if (!pinned)
            {
                if (_refused.Add(dir)) Errors.Add($"{Path.GetRelativePath(_owner.Folder, dir)} goes through a link, so nothing was written there.");
                return false;
            }
            if (!WindowerFiles.IsLinkOrFolder(target)) return true;
            Errors.Add($"{Path.GetRelativePath(_owner.Folder, target)} is a link or a folder, so it wasn't replaced.");
            return false;
        }

        public async Task Step(Func<Task> step)
        {
            try { await step(); }
            catch (Exception e) when (e is not OperationCanceledException || !Ct.IsCancellationRequested)
            {
                Errors.Add(_owner.Reason(e, "Provenance ran into a problem it didn't expect. Try again in a moment."));
            }
        }

        // right before a file goes in or out: the folders are still plain and still where they were
        public void Guard(string target)
        {
            var dir = Path.GetDirectoryName(target)!;
            if (!Pins.Intact(dir) || !Pins.Intact(_owner.TempDir) || WindowerFiles.IsLinkOrFolder(target))
                throw WindowerFiles.ThroughLink(Path.GetRelativePath(_owner.Folder, target));
        }

        public void Swap(string fresh, string target) => WindowerFiles.Swap(fresh, target, _owner.TempDir, () => Guard(target));

        public void Extract(ZipArchiveEntry entry, string target) => WindowerFiles.Extract(entry, target, _owner.TempDir, () => Guard(target));

        public string? Backup(string file) => WindowerFiles.Backup(file, _owner.TempDir, () => Guard(file));

        public void Move(string from, string to, bool overwrite = false)
        {
            Guard(to);
            File.Move(from, to, overwrite);
        }

        public void Delete(string path) => Pins.DeleteEntry(path);

        public void Cleanup()
        {
            Pins.Release(_owner.TempDir);
            WindowerFiles.DeleteTree(Pins, _owner.TempDir);
        }

        public void Dispose()
        {
            Http.Dispose();
            Pins.Dispose();
        }
    }

    private async Task<WindowerManifest> ManifestAsync(Job job, bool skip, WindowerUpdateResult result)
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
                var temp = WindowerFiles.NewTemp(TempDir);
                var since = switched ? WindowerHttp.NoFile : WindowerHttp.Modified(cache);
                var f = await job.Http.DownloadAsync(Servers.Files, $"{Branch}/manifest.xml", temp, since, job.Ct);
                if (!f.Ok) Note(job.Errors, "Windower's manifest", f);
                else if (TextFiles.LoadXml(temp) is not { } doc) job.Errors.Add("Windower's manifest from the server wasn't readable.");
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
    private async Task CoreAsync(Job job, WindowerManifest manifest, bool skip, WindowerUpdateResult result)
    {
        if (!skip && result.LauncherBefore != manifest.LauncherVersion &&
            await ReplaceAsync(job, $"{Branch}/Windower.exe", LauncherPath, "Windower's own launcher"))
        {
            result.LauncherAfter = WindowerFiles.LauncherVersion(LauncherPath);
            job.Http.UserAgent = WindowerHttp.UserAgentFor(result.LauncherAfter.Major > 0 ? result.LauncherAfter : WindowerHttp.DefaultLauncher);
        }
        // a missing Hook.dll can't be loaded by anything, so it comes down even while games run
        if ((!skip || !File.Exists(HookPath)) && WindowerFiles.FileVersion(HookPath) != manifest.HookVersion &&
            await ReplaceAsync(job, $"{Branch}/Hook.dll", HookPath, "Hook.dll"))
            result.HookAfter = WindowerFiles.FileVersion(HookPath);
    }

    private async Task<bool> ReplaceAsync(Job job, string url, string target, string what)
    {
        if (!job.Ready(target)) return false;
        var temp = WindowerFiles.NewTemp(TempDir);
        try
        {
            var f = await job.Http.DownloadAsync(Servers.Files, url, temp, null, job.Ct);
            if (!f.Ok)
            {
                Note(job.Errors, what, f);
                return false;
            }
            if (!WindowerFiles.LooksLikePe(temp))
            {
                job.Errors.Add($"The download for {what} isn't a program file, so the old one was kept.");
                return false;
            }
            job.Swap(temp, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            job.Errors.Add($"{what} couldn't be replaced, the old one was kept: {e.Message}");
            return false;
        }
        finally { job.Delete(temp); }
    }

    // runs with games up too. The old resources.xml goes first, so a run that stops part way leaves none and the next
    // one checks every file again
    private async Task ResAsync(Job job, WindowerUpdateResult result)
    {
        var resDir = Path.Combine(Folder, "res");
        var cache = Path.Combine(Folder, "updates", "resources.xml");
        if (!job.Pins.Pin(resDir))
        {
            job.Errors.Add("Windower's res folder is a link, so its resource files weren't checked.");
            return;
        }
        var temp = WindowerFiles.NewTemp(TempDir);
        var f = await job.Http.DownloadAsync(Servers.Res, "manifest.xml", temp, WindowerHttp.Modified(cache), job.Ct);
        if (!f.Ok)
        {
            Note(job.Errors, "The resource list", f);
            return;
        }
        if (TextFiles.LoadXml(temp) is not { } doc)
        {
            job.Delete(temp);
            job.Errors.Add("The resource list from the server wasn't readable.");
            return;
        }
        var names = WindowerManifest.ParseRes(doc);
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
            var t = WindowerFiles.NewTemp(TempDir);
            var r = await job.Http.DownloadAsync(Servers.Res, $"lua/{name}.lua", t, WindowerHttp.Modified(target), job.Ct);
            if (!r.Ok)
            {
                whole &= r.Outcome is WindowerFetchOutcome.NotModified or WindowerFetchOutcome.HttpError;
                Note(job.Errors, name + ".lua", r);
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

    private async Task PluginsAsync(Job job, WindowerManifest manifest, bool skip, WindowerUpdateResult result)
    {
        foreach (var p in manifest.Plugins)
        {
            job.Ct.ThrowIfCancellationRequested();
            if (!WindowerFiles.IsPlainName(p.Name))
            {
                job.Errors.Add($"The manifest names a plugin that isn't allowed ({p.Name}), so it was skipped.");
                continue;
            }
            var dll = Path.Combine(PluginsDir, p.Name + ".dll");
            bool missing = !File.Exists(dll);
            if (!p.Required && missing) continue;
            if (skip && !(p.Required && missing)) continue;
            if (WindowerFiles.FileVersion(dll) == p.Version) continue;
            var written = new List<string>();
            if (await PluginAsync(job, p, dll, install: false, written) == WindowerFetchOutcome.Downloaded) result.PluginsUpdated.Add(p.Name);
        }
    }

    // Zip first and the DLL last, once every file's in, so anything short gets tried again. Like Windower, an update
    // asks for the zip whatever the DLL got
    private async Task<WindowerFetchOutcome> PluginAsync(Job job, WindowerPlugin p, string dll, bool install, List<string> written)
    {
        if (!job.Ready(dll)) return WindowerFetchOutcome.Broken;
        var tempDll = WindowerFiles.NewTemp(TempDir);
        string? tempZip = null;
        try
        {
            var d = await job.Http.DownloadAsync(Servers.Files, $"{Branch}/plugins/{p.Name}.dll", tempDll,
                                                 install ? WindowerHttp.Modified(dll) : null, job.Ct);
            bool ok = d.Ok;
            if (!ok) Note(job.Errors, p.Name + ".dll", d);
            else if (!WindowerFiles.LooksLikePe(tempDll))
            {
                ok = false;
                job.Errors.Add($"The {p.Name}.dll that downloaded isn't a program file, so it wasn't used.");
            }
            if (p.Resources && (d.Ok || !install))
            {
                tempZip = WindowerFiles.NewTemp(TempDir);
                var z = await job.Http.DownloadAsync(Servers.Files, $"{Branch}/zips/{p.Name}.zip", tempZip, null, job.Ct);
                if (!z.Ok)
                {
                    ok = false;
                    Note(job.Errors, p.Name + ".zip", z);
                }
                else if (!WindowerFiles.IsZip(tempZip))
                {
                    ok = false;
                    job.Errors.Add($"The {p.Name}.zip that downloaded isn't a zip, so it wasn't used.");
                }
            }
            if (!ok) return d.Outcome == WindowerFetchOutcome.Downloaded ? WindowerFetchOutcome.Broken : d.Outcome;
            if (tempZip is not null && !ExtractPluginZip(job, tempZip, p.Name, skipThumbs: !install, written))
            {
                job.Errors.Add($"Not all of the files for {p.Name} could be written.");
                return WindowerFetchOutcome.Broken;
            }
            try { job.Swap(tempDll, dll); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                job.Errors.Add(WriteFailed(e, p.Name + ".dll"));
                return WindowerFetchOutcome.Broken;
            }
            written.Add(Path.Combine("plugins", p.Name + ".dll"));
            return WindowerFetchOutcome.Downloaded;
        }
        finally
        {
            job.Delete(tempDll);
            if (tempZip is not null) job.Delete(tempZip);
        }
    }

    // Windower extracts with Path.Combine and nothing else, so a zip could write anywhere. One entry that would land
    // outside plugins\ and none of it goes in
    private bool ExtractPluginZip(Job job, string zipFile, string plugin, bool skipThumbs, List<string> written)
    {
        using var zip = ZipFile.OpenRead(zipFile);
        var work = new List<(ZipArchiveEntry Entry, string Target)>();
        bool refused = false;
        foreach (var e in zip.Entries)
        {
            if (e.Name.Length == 0 || skipThumbs && e.Name == "Thumbs.db") continue;
            if (PathInside.Collapse(PluginsDir, e.FullName) is { } target) work.Add((e, target));
            else
            {
                refused = true;
                job.Errors.Add($"{plugin}.zip has a file that would land outside the plugins folder ({e.FullName.Replace('\0', '?')}).");
            }
        }
        if (refused) return false;
        bool all = true;
        foreach (var (e, target) in work)
        {
            if (!job.Ready(target))
            {
                all = false;
                continue;
            }
            try
            {
                job.Extract(e, target);
                written.Add(Path.GetRelativePath(Folder, target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                all = false;
                job.Errors.Add(WriteFailed(ex, $"{e.FullName} from {plugin}.zip"));
            }
        }
        return all;
    }

    // Changed files of installed addons are written, plus every lib, since Windower never counts libs as old files
    private async Task AddonsAsync(Job job, bool skip, WindowerUpdateResult result)
    {
        var cachePath = WindowerAddonTree.CachePath(Folder);
        var zipPath = WindowerAddonTree.ZipPath(Folder);
        // a tree without its zip counts as no cache, or the zip would never come back until upstream changed
        var cached = File.Exists(zipPath) ? WindowerAddonTree.Load(cachePath) : null;
        if (cached is not null) ExtractAddonsXml(job, zipPath);
        if (skip) return;

        var (b, body) = await job.Http.GetBytesAsync(Servers.GitHubApi, "branches/" + Branch, null, job.Ct);
        if (!b.Ok)
        {
            Note(job.Errors, "The addon list", b);
            return;
        }
        if (BranchTree(body!) is not { } head)
        {
            job.Errors.Add("GitHub's answer about the addon branch wasn't readable.");
            return;
        }
        if (cached?.Sha == head.Sha) return;
        if (!Uri.TryCreate(head.Url + "?recursive=1", UriKind.Absolute, out var treeUri) || treeUri.Host != Servers.GitHubApi.Host)
        {
            job.Errors.Add("GitHub pointed the addon list somewhere unexpected, so it wasn't fetched.");
            return;
        }

        var treeTemp = WindowerFiles.NewTemp(TempDir);
        var zipTemp = WindowerFiles.NewTemp(TempDir);
        try
        {
            var t = await job.Http.DownloadAsync(Servers.GitHubApi, treeUri.AbsoluteUri, treeTemp, null, job.Ct);
            if (!t.Ok)
            {
                Note(job.Errors, "The addon list", t);
                return;
            }
            if (WindowerAddonTree.Load(treeTemp) is not { } tree)
            {
                job.Errors.Add("The addon list from GitHub wasn't readable.");
                return;
            }
            var z = await job.Http.DownloadAsync(Servers.GitHubArchive, Branch + ".zip", zipTemp, WindowerHttp.Modified(zipPath), job.Ct);
            if (!z.Ok)
            {
                Note(job.Errors, "The addons", z);
                return;
            }
            if (!RepoZipFits(zipTemp))
            {
                job.Errors.Add("The addons zip from GitHub wasn't usable, so the addons weren't updated.");
                return;
            }
            if (!job.Ready(zipPath) || !job.Ready(cachePath)) return;
            job.Swap(zipTemp, zipPath);
            ExtractAddonsXml(job, zipPath);

            var old = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in cached?.Addons.SelectMany(a => a.Files) ?? []) old.TryAdd(f.Path, f.Sha);
            bool Changed(WindowerAddonFile f) => !old.TryGetValue(f.Path, out var sha) || sha != f.Sha;
            var libs = tree.Libs.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var addon in tree.Addons.Where(a => WindowerAddonTree.IsInstalled(a, Folder)))
                {
                    int n = 0;
                    foreach (var f in addon.Files.Where(f => !libs.Contains(f.Path) && Changed(f)))
                        if (WriteAddonFile(job, zip, f)) n++;
                    if (n > 0) result.AddonsUpdated.Add(addon.Name);
                }
                foreach (var f in tree.Libs.Where(Changed))
                    if (WriteAddonFile(job, zip, f)) result.LibFilesWritten++;
            }
            job.Swap(treeTemp, cachePath);
            result.AddonTreeChanged = true;
        }
        finally
        {
            job.Delete(treeTemp);
            job.Delete(zipTemp);
        }
    }

    private bool RepoZipFits(string file)
    {
        try
        {
            using var zip = ZipFile.OpenRead(file);
            var top = WindowerAddonTree.ZipEntry(Branch, "");
            return zip.Entries.Any(e => e.FullName.StartsWith(top, StringComparison.Ordinal));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }

    private bool WriteAddonFile(Job job, ZipArchive zip, WindowerAddonFile f)
    {
        var target = PathInside.Collapse(Folder, f.Path);
        if (target is null || !target.StartsWith(AddonsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            job.Errors.Add($"The addon list has a file outside the addons folder ({f.Path}), so it was skipped.");
            return false;
        }
        if (WindowerAddonTree.OnlyIfMissing(f.Path) && File.Exists(target)) return false;
        var entry = zip.GetEntry(WindowerAddonTree.ZipEntry(Branch, f.Path));
        if (entry is null)
        {
            job.Errors.Add($"{f.Path} isn't in the addons zip.");
            return false;
        }
        if (!job.Ready(target)) return false;
        try
        {
            job.Extract(entry, target);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            job.Errors.Add(WriteFailed(e, f.Path));
            return false;
        }
    }

    private void ExtractAddonsXml(Job job, string zipPath)
    {
        var target = Path.Combine(Folder, "updates", "addons.xml");
        string? temp = null;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.GetEntry(WindowerAddonTree.ZipEntry(Branch, "addons/addons.xml")) is not { } entry || !job.Ready(target)) return;
            temp = WindowerFiles.WriteEntry(entry, TempDir);
            job.Move(temp, target, overwrite: true);
            temp = null;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { }
        finally
        {
            if (temp is not null) job.Delete(temp);
        }
    }

    private static (string Sha, string Url)? BranchTree(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("commit", out var c1) && c1.ValueKind == JsonValueKind.Object &&
                c1.TryGetProperty("commit", out var c2) && c2.ValueKind == JsonValueKind.Object &&
                c2.TryGetProperty("tree", out var tree) && tree.ValueKind == JsonValueKind.Object &&
                tree.TryGetProperty("sha", out var sha) && sha.ValueKind == JsonValueKind.String &&
                tree.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                return (sha.GetString()!, url.GetString()!);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        return null;
    }

    private static void Note(List<string> errors, string what, WindowerFetch f)
    {
        switch (f.Outcome)
        {
            case WindowerFetchOutcome.HttpError:
                errors.Add($"{what} couldn't be downloaded, {f.Host} answered {f.Status}.");
                break;
            case WindowerFetchOutcome.Unreachable:
                errors.Add($"{f.Host} couldn't be reached, so nothing else was downloaded from it.");
                break;
            case WindowerFetchOutcome.Broken:
                errors.Add($"{what} didn't download completely, so it wasn't used.");
                break;
        }
    }

}
