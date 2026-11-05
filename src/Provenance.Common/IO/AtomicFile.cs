using System.Text;

namespace Provenance.Common.IO;

// Writes go to a temp file beside the target and get moved over it, so a reader never sees half a file and a crash
// leaves the old one. The temp name is new every time, so two writers at once can't trip over each other.
public static class AtomicFile
{
    public static string TempFor(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    public static void Write(string path, byte[] bytes) => Build(path, tmp => File.WriteAllBytes(tmp, bytes));

    public static void Write(string path, string text) =>
        Build(path, tmp => File.WriteAllText(tmp, text, new UTF8Encoding(false)));

    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken ct = default)
    {
        var tmp = TempFor(path);
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            Discard(tmp);
            throw;
        }
    }

    // for a file that takes more than a write (a copy that's then edited, a stream with its own settings)
    public static void Build(string path, Action<string> fill)
    {
        var tmp = TempFor(path);
        try
        {
            fill(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            Discard(tmp);
            throw;
        }
    }

    // never opened over anything that's already there
    internal static void WriteNew(string tmp, byte[] bytes, bool flushToDisk = false)
    {
        using var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        fs.Write(bytes);
        if (flushToDisk) fs.Flush(flushToDisk: true);
    }

    internal static void Discard(string tmp)
    {
        try { File.Delete(tmp); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
