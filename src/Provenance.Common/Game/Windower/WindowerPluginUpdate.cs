using System.IO.Compression;
using Provenance.Common.Security;

namespace Provenance.Common.Game.Windower;

internal sealed class WindowerPluginUpdate(WindowerUpdateFolder job, WindowerServers servers, string branch, WindowerScanCache? cache = null)
{
    private readonly string _pluginsDir = Path.Combine(job.Root, "plugins");

    public string DllPath(string name) => Path.Combine(_pluginsDir, name + ".dll");

    public async Task UpdateAllAsync(WindowerManifest manifest, bool skip, WindowerUpdateResult result)
    {
        foreach (var p in manifest.Plugins)
        {
            job.Ct.ThrowIfCancellationRequested();
            if (!WindowerFiles.IsPlainName(p.Name))
            {
                job.Errors.Add($"The manifest names a plugin that isn't allowed ({p.Name}), so it was skipped.");
                continue;
            }
            if (!Wants(p, skip)) continue;
            var written = new List<string>();
            if (await FetchAsync(p, DllPath(p.Name), install: false, written) == WindowerFetchOutcome.Downloaded) result.PluginsUpdated.Add(p.Name);
        }
    }

    public bool AnyToFetch(WindowerManifest manifest, bool skip) => manifest.Plugins.Any(p => WindowerFiles.IsPlainName(p.Name) && Wants(p, skip));

    private bool Wants(WindowerPlugin p, bool skip)
    {
        var dll = DllPath(p.Name);
        bool missing = !File.Exists(dll);
        if (!p.Required && missing) return false;
        if (skip && !(p.Required && missing)) return false;
        return WindowerFiles.FileVersion(dll, cache) != p.Version;
    }

    // Zip first and the DLL last, once every file's in, so anything short gets tried again. Like Windower, an update
    // asks for the zip whatever the DLL got
    public async Task<WindowerFetchOutcome> FetchAsync(WindowerPlugin p, string dll, bool install, List<string> written)
    {
        if (!job.Ready(dll)) return WindowerFetchOutcome.Broken;
        string? tempDll = null, tempZip = null;
        try
        {
            WindowerFetch d;
            (d, tempDll) = await job.FetchToTemp(servers.Files, $"{branch}/plugins/{p.Name}.dll", install ? WindowerHttp.Modified(dll) : null,
                                                 p.Name + ".dll", WindowerFiles.LooksLikePe,
                                                 $"The {p.Name}.dll that downloaded isn't a program file, so it wasn't used.");
            bool ok = tempDll is not null;
            if (p.Resources && (d.Ok || !install))
            {
                (_, tempZip) = await job.FetchToTemp(servers.Files, $"{branch}/zips/{p.Name}.zip", null, p.Name + ".zip", WindowerFiles.IsZip,
                                                     $"The {p.Name}.zip that downloaded isn't a zip, so it wasn't used.");
                if (tempZip is null) ok = false;
            }
            if (!ok) return d.Outcome == WindowerFetchOutcome.Downloaded ? WindowerFetchOutcome.Broken : d.Outcome;
            if (tempZip is not null && !ExtractZip(tempZip, p.Name, skipThumbs: !install, written))
            {
                job.Errors.Add($"Not all of the files for {p.Name} could be written.");
                return WindowerFetchOutcome.Broken;
            }
            try { job.Swap(tempDll!, dll); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                job.Errors.Add(WindowerUpdateFolder.WriteFailed(e, p.Name + ".dll"));
                return WindowerFetchOutcome.Broken;
            }
            written.Add(Path.Combine("plugins", p.Name + ".dll"));
            return WindowerFetchOutcome.Downloaded;
        }
        finally
        {
            if (tempDll is not null) job.Delete(tempDll);
            if (tempZip is not null) job.Delete(tempZip);
        }
    }

    // Windower extracts with Path.Combine and nothing else, so a zip could write anywhere. One entry that would land
    // outside plugins\ and none of it goes in
    private bool ExtractZip(string zipFile, string plugin, bool skipThumbs, List<string> written)
    {
        using var zip = ZipFile.OpenRead(zipFile);
        var work = new List<(ZipArchiveEntry Entry, string Target)>();
        bool refused = false;
        foreach (var e in zip.Entries)
        {
            if (e.Name.Length == 0 || skipThumbs && e.Name == "Thumbs.db") continue;
            if (PathInside.Collapse(_pluginsDir, e.FullName) is { } target) work.Add((e, target));
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
                written.Add(Path.GetRelativePath(job.Root, target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                all = false;
                job.Errors.Add(WindowerUpdateFolder.WriteFailed(ex, $"{e.FullName} from {plugin}.zip"));
            }
        }
        return all;
    }
}
