using System.Security.Cryptography;
using Provenance.Common.IO;
using Provenance.Common.Mirror;
using Provenance.Common.PolFormat;
using Serilog;

namespace Provenance.Common.Patcher;

// kept as the mirror sent it and checked on every load, so nothing in the app's folder is trusted for being there
internal static class SignedBuilds
{
    private static readonly object Gate = new();
    private static MirrorBuilds? _held;
    private static bool _loaded;
    private static int _asked;
    private static string? _path = AppPaths.UserData is { } dir ? Path.Combine(dir, "viewer-builds") : null;
    private static IReadOnlyDictionary<string, ECDsa>? _trusted;
    private static DateTimeOffset _floor = MirrorFloor.Published;

    internal static Func<CancellationToken, Task> Fetch = ct => RefreshAsync(MirrorClient.Shared, ct);

    public static IReadOnlyList<MirrorBuild> Entries()
    {
        lock (Gate)
        {
            Load();
            return _held?.Builds ?? [];
        }
    }

    internal static ViewerBuild.Builds Accepted() => ViewerBuild.Known with { Signed = Entries() };

    public static ViewerBuild.State Check(string viewerDir) => ViewerBuild.Check(viewerDir, ViewerBuild.RegionOf(viewerDir), Accepted());

    public static void FetchIfUnknown(string viewerDir, CancellationToken ct)
    {
        try
        {
            if (Check(viewerDir) != ViewerBuild.State.Unknown || Interlocked.Exchange(ref _asked, 1) == 1) return;
            Fetch(ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Interlocked.Exchange(ref _asked, 0); }
        catch (Exception ex)
        {
            Log.Information("The mirror's build list didn't come: {Reason}", ex.Message);
        }
    }

    public static IDisposable? Hold(string viewerDir, out string? problem) =>
        ViewerBuild.Hold(viewerDir, ViewerBuild.RegionOf(viewerDir), Accepted(), out problem);

    public static async Task RefreshAsync(MirrorClient client, CancellationToken ct)
    {
        Interlocked.Exchange(ref _asked, 1);
        try
        {
            if (await client.GetBuildsAsync(ct) is { } got) Offer(got.List, got.Blob);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            Log.Information("The mirror's build list didn't come: {Reason}", ex.Message);
        }
    }

    internal static bool Offer(MirrorBuilds list, byte[] blob)
    {
        lock (Gate)
        {
            Load();
            if (list.Published < _floor || (_held is not null && list.Published <= _held.Published)) return false;
            _held = list;
            Save(blob);
            return true;
        }
    }

    internal static void Reset(string? path, IReadOnlyDictionary<string, ECDsa>? trusted, DateTimeOffset? floor = null)
    {
        lock (Gate)
        {
            (_path, _trusted, _held, _loaded, _asked, _floor) = (path, trusted, null, false, 0, floor ?? MirrorFloor.Published);
        }
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (_path is not null && File.Exists(_path) && new FileInfo(_path).Length <= MirrorClient.MaxSignedBytes)
                _held = MirrorBuilds.FromSigned(File.ReadAllBytes(_path), _trusted ?? MirrorKeys.Trusted) is { } kept && kept.Published >= _floor ? kept : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Save(byte[] blob)
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.Write(_path, blob);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
