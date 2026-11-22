using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Provenance.Common.IO;
using Provenance.Common.Mirror;
using Serilog;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Provenance.Wpf.Services;

internal sealed class ReleaseRefusedException(string reason) : Exception(reason);

// a beta copy reads both channels, Velopack never offers an older one so it moves up to a newer stable
internal sealed class ChannelSource(string url, SemanticVersion? installed, IFileDownloader? downloader = null,
                                    IReadOnlyDictionary<string, ECDsa>? trusted = null,
                                    Action<IReadOnlyList<SemanticVersion>>? feedRead = null, TimeSpan? firstWait = null)
    : SimpleWebSource(url, downloader)
{
    public const string Stable = AppRelease.Stable;
    public const string Beta = AppRelease.Beta;
    public const string Releases = "https://api.github.com/repos/Daleterrence/Provenance/releases?per_page=10";
    public const string ReleaseFiles = "https://github.com/Daleterrence/Provenance/releases/download/";

    private readonly TimeSpan _firstWait = firstWait ?? TimeSpan.FromSeconds(10);

    // the downloader's timeout is in minutes
    private const double StatementMinutes = 0.5;

    private readonly IReadOnlyDictionary<string, ECDsa> _trusted = trusted ?? MirrorKeys.Trusted;
    private readonly Dictionary<string, byte[]> _statements = new(StringComparer.Ordinal);

    // the same signed files sit on each GitHub release, for when the update server can't be reached
    private (Uri Files, string[] Feeds)? _second;

    public override Uri BaseUri => _second?.Files ?? base.BaseUri;

    public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
                                                                 Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        VelopackAsset[] assets;
        try { assets = await InTime(Feeds(logger, appId, stagingId, latestLocalRelease)); }
        catch (Exception first) when (_second is null && NoAnswer(first))
        {
            _second = await SecondAsync(first);
            assets = await Feeds(logger, appId, stagingId, latestLocalRelease);
        }
        // only full packages are ever published, and Velopack runs a delta through Update.exe before anything's checked
        var full = assets.Where(a => a.Type == VelopackAssetType.Full).ToArray();
        feedRead?.Invoke([.. full.Select(a => a.Version)]);
        return new VelopackAssetFeed { Assets = full };
    }

    private async Task<VelopackAsset[]> Feeds(IVelopackLogger logger, string? appId, Guid? stagingId, VelopackAsset? latest)
    {
        Task<VelopackAssetFeed>[] reads;
        if (_second is { } second) reads = [.. second.Feeds.Select(c => base.GetReleaseFeed(logger, appId, c, stagingId, latest))];
        else if (installed?.IsPrerelease != true) reads = [base.GetReleaseFeed(logger, appId, Stable, stagingId, latest)];
        else reads = [StableOrNothing(logger, appId, stagingId, latest), base.GetReleaseFeed(logger, appId, Beta, stagingId, latest)];
        return [.. (await Task.WhenAll(reads)).SelectMany(f => f.Assets)];
    }

    // a blocked connection takes over a minute to fail by itself
    private async Task<T> InTime<T>(Task<T> work)
    {
        if (_second is not null || await Task.WhenAny(work, Task.Delay(_firstWait)) == work) return await work;
        _ = work.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        throw new TimeoutException("The update server didn't answer in time.");
    }

    // an answer with an error in it is still the server's say, only silence sends the check elsewhere
    private static bool NoAnswer(Exception e) => e is TimeoutException or TaskCanceledException or HttpRequestException { StatusCode: null };

    private async Task<(Uri Files, string[] Feeds)> SecondAsync(Exception first)
    {
        Log.Information("The update server gave no answer ({Reason}), looking at the releases on GitHub", first.Message);
        bool betas = installed?.IsPrerelease == true;
        string list = await Downloader.DownloadString(Releases, null, StatementMinutes);
        try
        {
            using var releases = JsonDocument.Parse(list);
            foreach (var release in releases.RootElement.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean() || (release.GetProperty("prerelease").GetBoolean() && !betas)) continue;
                var files = release.GetProperty("assets").EnumerateArray().Select(a => a.GetProperty("name").GetString()).ToHashSet();
                string[] feeds = [.. new[] { Stable, Beta }.Where(c => (c == Stable || betas) && files.Contains($"releases.{c}.json"))];
                if (feeds.Length == 0 || release.GetProperty("tag_name").GetString() is not { Length: > 0 } tag) continue;
                Log.Information("Reading release {Tag} on GitHub", tag);
                return (new Uri(ReleaseFiles + Uri.EscapeDataString(tag) + "/"), feeds);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.Warning("GitHub's list of releases didn't read: {Reason}", e.Message);
        }
        Log.Warning("No release on GitHub for this copy to read");
        throw first;
    }

    // Velopack does nothing with localFile until this returns, so this is the last point before its own hash check
    public override async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress,
                                                    CancellationToken cancelToken)
    {
        try
        {
            RefuseOddName(releaseEntry);
            await base.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
            Refuse(releaseEntry, await FetchStatementAsync(releaseEntry, cancelToken), localFile);
        }
        catch (ReleaseRefusedException)
        {
            FileDelete.Try(localFile);
            throw;
        }
    }

    public async Task RecheckAsync(VelopackAsset asset, string? packagePath, CancellationToken ct)
    {
        try
        {
            RefuseOddName(asset);
            byte[]? held;
            lock (_statements) _statements.TryGetValue(asset.FileName, out held);
            Refuse(asset, held ?? await FetchStatementAsync(asset, ct), packagePath);
        }
        catch (ReleaseRefusedException)
        {
            if (packagePath is not null) FileDelete.Try(packagePath);
            throw;
        }
    }

    private static void RefuseOddName(VelopackAsset asset)
    {
        if (asset.Type != VelopackAssetType.Full) throw new ReleaseRefusedException($"{asset.FileName} isn't a full package");
        var version = asset.Version?.ToString() ?? "";
        if (asset.FileName != AppRelease.PackageName(version, Stable) && asset.FileName != AppRelease.PackageName(version, Beta))
            throw new ReleaseRefusedException($"the feed names {asset.FileName} for version {version}, which isn't a package name");
    }

    private void Refuse(VelopackAsset asset, byte[] statement, string? packagePath)
    {
        if (AppRelease.Check(statement, _trusted, installed?.IsPrerelease == true, asset.Version.ToString(), asset.FileName,
                             asset.Type == VelopackAssetType.Full, packagePath) is { } why)
            throw new ReleaseRefusedException(why);
        Log.Information("Provenance {Version} matches the statement signed with {KeyId}", asset.Version, SignedBlob.KeyId(statement));
    }

    private async Task<byte[]> FetchStatementAsync(VelopackAsset asset, CancellationToken ct)
    {
        var name = AppRelease.StatementName(asset.FileName);
        var at = new Uri(new Uri(BaseUri.AbsoluteUri.TrimEnd('/') + "/"), Uri.EscapeDataString(name)).AbsoluteUri;
        byte[] statement;
        try { statement = await Downloader.DownloadBytes(at, null, StatementMinutes).WaitAsync(ct); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new ReleaseRefusedException($"there's no statement for {asset.FileName} on the update server");
        }
        if (statement.Length > AppRelease.MaxBytes) throw new ReleaseRefusedException($"{name} is {statement.Length} bytes, over the {AppRelease.MaxBytes} allowed");
        lock (_statements) _statements[asset.FileName] = statement;
        return statement;
    }

    // the stable feed can be missing while there are only betas. Any other missing feed means the server's wrong, so that fails the check
    private async Task<VelopackAssetFeed> StableOrNothing(IVelopackLogger logger, string? appId, Guid? stagingId,
                                                          VelopackAsset? latestLocalRelease)
    {
        try { return await base.GetReleaseFeed(logger, appId, Stable, stagingId, latestLocalRelease); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            Log.Information("No stable release on the update server yet");
            return new VelopackAssetFeed();
        }
    }
}
