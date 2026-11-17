using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Provenance.Common.Mirror;
using Velopack;
using Velopack.Sources;

namespace Provenance.ReleaseCheck;

// The app's own check on a release, before it goes up and on what the update server serves once it has. A release
// this passes is one every copy that trusts these keys takes
internal static class ReleaseGate
{
    public const string UpdateServer = "https://updates.provenancexi.app/";

    // same rule as tools\pack-release.ps1 and the app, a suffix means beta
    public static string ChannelOf(string version) => version.Contains('-') ? AppRelease.Beta : AppRelease.Stable;

    // a release's files in one folder, as the GitHub release has them
    public static bool CheckFolder(string folder, string version, IReadOnlyDictionary<string, ECDsa> trusted, TextWriter log)
    {
        var channel = ChannelOf(version);
        var name = AppRelease.PackageName(version, channel);
        var package = Path.Combine(folder, name);
        var feedFile = Path.Combine(folder, $"releases.{channel}.json");
        var statement = Path.Combine(folder, AppRelease.StatementName(name));
        foreach (var file in new[] { package, feedFile, statement })
            if (!File.Exists(file)) return Fail(log, $"{Path.GetFileName(file)} isn't in {folder}");
        if (new FileInfo(statement).Length > AppRelease.MaxBytes) return Fail(log, $"{Path.GetFileName(statement)} is over {AppRelease.MaxBytes} bytes");
        VelopackAsset[] assets;
        try { assets = VelopackAssetFeed.FromJson(File.ReadAllText(feedFile)).Assets; }
        catch (Exception e) when (e is not OutOfMemoryException) { return Fail(log, $"releases.{channel}.json doesn't read: {e.Message}"); }
        var entries = assets.Where(a => a.FileName == name).ToList();
        if (entries.Count != 1) return Fail(log, $"releases.{channel}.json has {entries.Count} entries for {name}, not 1");
        if (entries[0].Version?.ToString() != version) return Fail(log, $"releases.{channel}.json says {name} is version {entries[0].Version}");
        return CheckAsset(entries[0], channel, package, File.ReadAllBytes(statement), trusted, log);
    }

    // each channel's newest full package and its statement, fetched the way a copy fetches them, CDN and all
    public static async Task<bool> CheckLiveAsync(string server, IReadOnlyDictionary<string, ECDsa> trusted, IFileDownloader http, TextWriter log)
    {
        var root = server.EndsWith('/') ? server : server + "/";
        var temp = Directory.CreateTempSubdirectory("release-check-");
        try
        {
            bool ok = true, any = false;
            foreach (var channel in new[] { AppRelease.Stable, AppRelease.Beta })
            {
                string json;
                try { json = await http.DownloadString($"{root}releases.{channel}.json", timeout: 1); }
                catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                    log.WriteLine($"{channel}: no feed on {root}");
                    continue;
                }
                var newest = VelopackAssetFeed.FromJson(json).Assets.Where(a => a.Type == VelopackAssetType.Full).MaxBy(a => a.Version);
                if (newest is null)
                {
                    log.WriteLine($"{channel}: no full package on the feed");
                    continue;
                }
                any = true;
                // checked before anything's fetched, an address in there would send the download somewhere else
                if (newest.FileName != AppRelease.PackageName(newest.Version.ToString(), channel))
                {
                    ok = Fail(log, $"{channel}: the feed names {newest.FileName} for {newest.Version}");
                    continue;
                }
                var package = Path.Combine(temp.FullName, newest.FileName);
                await http.DownloadFile(root + Uri.EscapeDataString(newest.FileName), package, _ => { }, timeout: 30);
                byte[] statement;
                try { statement = await http.DownloadBytes(root + Uri.EscapeDataString(AppRelease.StatementName(newest.FileName)), timeout: 1); }
                catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                    ok = Fail(log, $"{channel}: no statement for {newest.FileName} on {root}");
                    continue;
                }
                ok &= CheckAsset(newest, channel, package, statement, trusted, log);
            }
            return any ? ok : Fail(log, $"{root} has no release on either channel");
        }
        finally
        {
            try { temp.Delete(recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // the feed's own size and hash the way Velopack checks them, then the statement the way the app does
    private static bool CheckAsset(VelopackAsset asset, string channel, string package, byte[] statement,
                                   IReadOnlyDictionary<string, ECDsa> trusted, TextWriter log)
    {
        var bytes = File.ReadAllBytes(package);
        if (asset.Type != VelopackAssetType.Full) return Fail(log, $"{asset.FileName} is a {asset.Type} package on the feed");
        if (asset.Size != bytes.Length) return Fail(log, $"the feed says {asset.FileName} is {asset.Size} bytes, it's {bytes.Length}");
        var feedHashOk = string.IsNullOrEmpty(asset.SHA256)
            ? string.Equals(asset.SHA1, Convert.ToHexString(SHA1.HashData(bytes)), StringComparison.OrdinalIgnoreCase)
            : asset.SHA256 == Convert.ToHexString(SHA256.HashData(bytes));
        if (!feedHashOk) return Fail(log, $"the feed's hash for {asset.FileName} isn't the package's");
        var why = AppRelease.Check(statement, trusted, channel == AppRelease.Beta, asset.Version.ToString(), asset.FileName,
                                   asset.Type == VelopackAssetType.Full, package);
        if (why is not null) return Fail(log, $"{asset.FileName}: {why}");
        var signed = AppRelease.FromSigned(statement, trusted)!;
        log.WriteLine($"ok: {asset.FileName}, {signed.Size} bytes, SHA-256 {signed.Sha256}, signed with {SignedBlob.KeyId(statement)} at {signed.Published:u}");
        return true;
    }

    private static bool Fail(TextWriter log, string why)
    {
        log.WriteLine("FAILED: " + why);
        return false;
    }
}
