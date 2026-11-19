using System.IO.Compression;
using System.Text.Json;
using Provenance.Common.Security;

namespace Provenance.Common.Game.Windower;

internal sealed class WindowerAddonUpdate(WindowerUpdateFolder job, WindowerServers servers, string branch)
{
    private readonly string _addonsDir = Path.Combine(job.Root, "addons");

    public async Task UpdateAsync(bool skip, WindowerUpdateResult result)
    {
        var cachePath = WindowerAddonTree.CachePath(job.Root);
        var zipPath = WindowerAddonTree.ZipPath(job.Root);
        // a tree without its zip counts as no cache, or the zip would never come back until upstream changed
        var cached = File.Exists(zipPath) ? WindowerAddonTree.Load(cachePath) : null;
        if (cached is not null) ExtractAddonsXml(zipPath);
        if (skip) return;

        var (b, body) = await job.Http.GetBytesAsync(servers.GitHubApi, "branches/" + branch, null, job.Ct);
        if (!b.Ok)
        {
            job.Note("The addon list", b);
            return;
        }
        if (BranchTree(body!) is not { } head)
        {
            job.Errors.Add("GitHub's answer about the addon branch wasn't readable.");
            return;
        }
        if (cached?.Sha == head.Sha) return;
        if (!Uri.TryCreate(head.Url + "?recursive=1", UriKind.Absolute, out var treeUri) || treeUri.Host != servers.GitHubApi.Host ||
            treeUri.Scheme != servers.GitHubApi.Scheme)
        {
            job.Errors.Add("GitHub pointed the addon list somewhere unexpected, so it wasn't fetched.");
            return;
        }

        string? treeTemp = null, zipTemp = null;
        try
        {
            (_, treeTemp) = await job.FetchToTemp(servers.GitHubApi, treeUri.AbsoluteUri, null, "The addon list");
            if (treeTemp is null) return;
            if (WindowerAddonTree.Load(treeTemp) is not { } tree)
            {
                job.Errors.Add("The addon list from GitHub wasn't readable.");
                return;
            }
            (_, zipTemp) = await job.FetchToTemp(servers.GitHubArchive, branch + ".zip", WindowerHttp.Modified(zipPath), "The addons",
                                                 RepoZipFits, "The addons zip from GitHub wasn't usable, so the addons weren't updated.");
            if (zipTemp is null) return;
            if (!job.Ready(zipPath) || !job.Ready(cachePath)) return;
            job.Swap(zipTemp, zipPath);
            ExtractAddonsXml(zipPath);

            var old = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in cached?.Addons.SelectMany(a => a.Files) ?? []) old.TryAdd(f.Path, f.Sha);
            bool Changed(WindowerAddonFile f) => !old.TryGetValue(f.Path, out var sha) || sha != f.Sha;
            var libs = tree.Libs.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var addon in tree.Addons.Where(a => WindowerAddonTree.IsInstalled(a, job.Root)))
                {
                    int n = 0;
                    foreach (var f in addon.Files.Where(f => !libs.Contains(f.Path) && Changed(f)))
                        if (WriteFile(zip, f)) n++;
                    if (n > 0) result.AddonsUpdated.Add(addon.Name);
                }
                foreach (var f in tree.Libs.Where(Changed))
                    if (WriteFile(zip, f)) result.LibFilesWritten++;
            }
            job.Swap(treeTemp, cachePath);
            result.AddonTreeChanged = true;
        }
        finally
        {
            if (treeTemp is not null) job.Delete(treeTemp);
            if (zipTemp is not null) job.Delete(zipTemp);
        }
    }

    private bool RepoZipFits(string file)
    {
        try
        {
            using var zip = ZipFile.OpenRead(file);
            var top = WindowerAddonTree.ZipEntry(branch, "");
            return zip.Entries.Any(e => e.FullName.StartsWith(top, StringComparison.Ordinal));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }

    public bool WriteFile(ZipArchive zip, WindowerAddonFile f)
    {
        var target = PathInside.Collapse(job.Root, f.Path);
        if (target is null || !PathInside.IsUnder(_addonsDir, target))
        {
            job.Errors.Add($"The addon list has a file outside the addons folder ({f.Path}), so it was skipped.");
            return false;
        }
        if (WindowerAddonTree.OnlyIfMissing(f.Path) && File.Exists(target)) return false;
        var entry = zip.GetEntry(WindowerAddonTree.ZipEntry(branch, f.Path));
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
            job.Errors.Add(WindowerUpdateFolder.WriteFailed(e, f.Path));
            return false;
        }
    }

    private void ExtractAddonsXml(string zipPath)
    {
        var target = Path.Combine(job.Root, "updates", "addons.xml");
        string? temp = null;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.GetEntry(WindowerAddonTree.ZipEntry(branch, "addons/addons.xml")) is not { } entry || !job.Ready(target)) return;
            temp = job.WriteEntry(entry);
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
}
