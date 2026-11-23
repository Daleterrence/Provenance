using System.Text.RegularExpressions;
using Provenance.Common.Game;
using Provenance.Common.Install;
using Provenance.Common.Mirror;
using Provenance.Common.Patcher;
using Serilog;

namespace Provenance.Common.PolFormat;

// the Japan-only profile pictures, kept in a folder of our own because the other regions' viewers don't ship them
public static partial class ProfilePictureExtras
{
    public const string DoneFile = "jp-pictures.done";

    private const string Region = "jp";
    private static int _generation, _started;

    public static string Folder => Path.Combine(AppPaths.ProgramData, "profile-pictures");

    public static int Generation => Volatile.Read(ref _generation);

    internal static void Arrived() => Interlocked.Increment(ref _generation);

    public static void Need(string viewerDir)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await FetchAsync(MirrorClient.Shared, viewerDir, Folder, CancellationToken.None); }
            catch (Exception ex) { Log.Information(ex, "Profile pictures: the Japan-only ones couldn't be fetched"); }
        });
    }

    public static async Task<bool> FetchAsync(MirrorClient client, string viewerDir, string folder, CancellationToken ct)
    {
        try
        {
            if (PolEnv.Region(viewerDir) == PolRegion.Japan || File.Exists(Path.Combine(folder, DoneFile))) return false;
            var product = GameProduct.Viewer.Slug;
            if (await client.GetPointerAsync(Region, ct) is not { } pointer || !pointer.Versions.TryGetValue(product, out var version)) return false;
            if (await client.GetIndexAsync(Region, product, version, ct) is not { } index) return false;
            if (Wanted(index, viewerDir) is not { } files) return false;
            var work = index with { Files = files };
            if (MirrorDownloads.Unusable(folder, work) is { } why)
            {
                Log.Warning("Profile pictures: {Why}", why);
                return false;
            }
            var missing = await MirrorDownloads.ScanAsync(folder, work, null, ct);
            if (missing.Count > 0)
            {
                var failed = await MirrorDownloads.DownloadAllAsync(client, Region, folder, missing, MirrorDownloads.DefaultParallel, null, ct,
                    (e, ex) => Log.Information("Profile pictures: {Path} didn't come down: {Why}", e.Path, ex.Message));
                if (failed.Count > 0) return false;
            }
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, DoneFile), version);
            Arrived();
            Log.Information("Profile pictures: {Count} Japan-only files in {Folder}", files.Count, folder);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Information("Profile pictures: the Japan-only ones didn't come down this time: {Why}", ex.Message);
            return false;
        }
    }

    // the two catalogue files and every sheet the viewer in this folder doesn't have
    private static List<MirrorEntry>? Wanted(MirrorIndex index, string viewerDir)
    {
        MirrorEntry? faces = null, sets = null;
        var sheets = new List<MirrorEntry>();
        foreach (var e in index.Files)
        {
            var path = e.Path.Replace('\\', '/');
            if (path.Equals("viewer/data/common/proface0.txt", StringComparison.OrdinalIgnoreCase)) sets = e;
            else if (path.Equals("viewer/data/common/proface1.txt", StringComparison.OrdinalIgnoreCase)) faces = e;
            else if (SheetFile().Match(path) is { Success: true } m && ProfilePictureSheets.SheetPath(viewerDir, int.Parse(m.Groups[1].Value)) is null) sheets.Add(e);
        }
        return faces is null || sets is null ? null : [sets, faces, .. sheets];
    }

    [GeneratedRegex(@"^data/icon/download/hnf(\d{3})\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex SheetFile();
}
