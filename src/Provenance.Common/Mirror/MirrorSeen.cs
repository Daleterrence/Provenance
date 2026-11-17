using System.Text.Json;
using Provenance.Common.IO;

namespace Provenance.Common.Mirror;

// The newest pointer time taken for each region, so an older signed pointer can't be served again. It sits next to
// viewer-builds, which outlives a reinstall. Never throws, a file that won't read means nothing's remembered
public sealed class MirrorSeen(string? path, DateTimeOffset floor)
{
    private const int MaxFileBytes = 64 * 1024;

    public static MirrorSeen Shared { get; } =
        new(AppPaths.UserData is { } dir ? Path.Combine(dir, "mirror-seen") : null, MirrorFloor.Published);

    private readonly Lock _gate = new();
    private Dictionary<string, DateTimeOffset>? _seen;

    public DateTimeOffset Floor => floor;

    // an equal time passes, that's the same pointer fetched again
    public bool Accept(string region, DateTimeOffset published)
    {
        lock (_gate)
        {
            _seen ??= Read();
            bool held = _seen.TryGetValue(region, out var mark);
            if (published < (held && mark > floor ? mark : floor)) return false;
            if (!held || published > mark)
            {
                _seen[region] = published;
                Write(_seen);
            }
            return true;
        }
    }

    private Dictionary<string, DateTimeOffset> Read()
    {
        try
        {
            if (path is not null && File.Exists(path) && new FileInfo(path).Length <= MaxFileBytes
                && JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllBytes(path)) is { } got)
                return new(got, StringComparer.Ordinal);
        }
        catch (Exception) { }
        return new(StringComparer.Ordinal);
    }

    // two copies writing at once can lose one's time, which only ever lowers a mark
    private void Write(Dictionary<string, DateTimeOffset> seen)
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(seen));
        }
        catch (Exception) { }
    }
}
