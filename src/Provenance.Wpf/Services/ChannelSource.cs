using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
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
                                    Action<IReadOnlyList<SemanticVersion>>? feedRead = null)
    : SimpleWebSource(url, downloader)
{
    public const string Stable = AppRelease.Stable;
    public const string Beta = AppRelease.Beta;

    // the downloader's timeout is in minutes
    private const double StatementMinutes = 0.5;

    private readonly IReadOnlyDictionary<string, ECDsa> _trusted = trusted ?? MirrorKeys.Trusted;
    private readonly Dictionary<string, byte[]> _statements = new(StringComparer.Ordinal);

    public override async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
                                                                 Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        VelopackAsset[] assets;
        if (installed?.IsPrerelease != true)
            assets = (await base.GetReleaseFeed(logger, appId, Stable, stagingId, latestLocalRelease)).Assets;
        else
        {
            var feeds = await Task.WhenAll(StableOrNothing(logger, appId, stagingId, latestLocalRelease),
                                           base.GetReleaseFeed(logger, appId, Beta, stagingId, latestLocalRelease));
            assets = [.. feeds.SelectMany(f => f.Assets)];
        }
        // only full packages are ever published, and Velopack runs a delta through Update.exe before anything's checked
        var full = assets.Where(a => a.Type == VelopackAssetType.Full).ToArray();
        feedRead?.Invoke([.. full.Select(a => a.Version)]);
        return new VelopackAssetFeed { Assets = full };
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
