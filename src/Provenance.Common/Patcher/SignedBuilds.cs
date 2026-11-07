using System.Security.Cryptography;
using Provenance.Common.Mirror;
using Provenance.Common.PolFormat;
using Serilog;

namespace Provenance.Common.Patcher;

// The builds the mirror has signed off on. The file's kept as the mirror sent it and checked again on every load, so
// nothing in the app's folder is trusted just for being there.
internal static class SignedBuilds
{
    private static readonly object Gate = new();
    private static MirrorBuilds? _held;
    private static bool _loaded;
    private static int _asked;
    private static string? _path = AppPaths.UserData is { } dir ? Path.Combine(dir, "viewer-builds") : null;
    private static IReadOnlyDictionary<string, ECDsa>? _trusted;

    internal static Func<CancellationToken, Task> Fetch = ct => RefreshAsync(GameUpdate.Mirror, ct);

    public static IReadOnlyList<MirrorBuild> Entries()
    {
        lock (Gate)
        {
            Load();
            return _held?.Builds ?? [];
        }
    }

    internal static ViewerBuild.Builds Accepted() => ViewerBuild.Known with { Signed = Entries() };

    // no network, the built-in list and whatever's held
    public static ViewerBuild.State Check(string viewerDir) => ViewerBuild.Check(viewerDir, ViewerBuild.RegionOf(viewerDir), Accepted());

    // a build nobody's listed gets one look at the mirror's list per run
    public static void FetchIfUnknown(string viewerDir, CancellationToken ct)
    {
        try
        {
            if (Check(viewerDir) != ViewerBuild.State.Unknown || Interlocked.Exchange(ref _asked, 1) == 1) return;
            Fetch(ct).GetAwaiter().GetResult();
        }
        // a cancelled lookup didn't happen, so the next login asks again
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Interlocked.Exchange(ref _asked, 0); }
        catch (Exception ex)
        {
            Log.Information("The mirror's build list didn't come: {Reason}", ex.Message);
        }
    }

    public static IDisposable? Hold(string viewerDir, CancellationToken ct, out string? problem)
    {
        FetchIfUnknown(viewerDir, ct);
        return ViewerBuild.Hold(viewerDir, ViewerBuild.RegionOf(viewerDir), Accepted(), out problem);
    }

    // a failed fetch leaves what's held alone, the built-in list works with no mirror at all
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

    // An older list than the one held is ignored. With nothing newer held, an older signed list is taken, so a pulled
    // build can come back until the next fetch.
    internal static bool Offer(MirrorBuilds list, byte[] blob)
    {
        lock (Gate)
        {
            Load();
            if (_held is not null && list.Published <= _held.Published) return false;
            _held = list;
            Save(blob);
            return true;
        }
    }

    internal static void Reset(string? path, IReadOnlyDictionary<string, ECDsa>? trusted)
    {
        lock (Gate)
        {
            (_path, _trusted, _held, _loaded, _asked) = (path, trusted, null, false, 0);
        }
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (_path is not null && File.Exists(_path) && new FileInfo(_path).Length <= MirrorClient.MaxSignedBytes)
                _held = MirrorBuilds.FromSigned(File.ReadAllBytes(_path), _trusted ?? MirrorKeys.Trusted);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Save(byte[] blob)
    {
        if (_path is null) return;
        var temp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(temp, blob);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception) { }
        }
    }
}
