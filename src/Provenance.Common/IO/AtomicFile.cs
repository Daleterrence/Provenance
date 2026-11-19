using System.Text;

namespace Provenance.Common.IO;

public static class AtomicFile
{
    public static string TempFor(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    public static void Write(string path, byte[] bytes) => Build(path, tmp => WriteNew(tmp, bytes, flushToDisk: true));

    public static void Write(string path, string text) =>
        Build(path, tmp => WriteNew(tmp, new UTF8Encoding(false).GetBytes(text), flushToDisk: true));

    public static Task WriteAsync(string path, byte[] bytes, CancellationToken ct = default) =>
        BuildAsync(path, tmp => File.WriteAllBytesAsync(tmp, bytes, ct));

    public static void Build(string path, Action<string> fill) =>
        BuildAsync(path, tmp => { fill(tmp); return Task.CompletedTask; }).GetAwaiter().GetResult();

    public static async Task BuildAsync(string path, Func<string, Task> fill)
    {
        var tmp = TempFor(path);
        try
        {
            await fill(tmp).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            FileDelete.Try(tmp);
            throw;
        }
    }

    public static bool CreateNew(string path, byte[] bytes)
    {
        var tmp = TempFor(path);
        try
        {
            WriteNew(tmp, bytes, flushToDisk: true);
            try
            {
                File.Move(tmp, path, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(path)) { return false; }
        }
        finally { FileDelete.Try(tmp); }
    }

    public static FileAttributes ClearReadOnly(string path)
    {
        if (!File.Exists(path)) return 0;
        var attrs = File.GetAttributes(path);
        if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        return attrs;
    }

    public static void DeleteReadOnly(string path)
    {
        ClearReadOnly(path);
        File.Delete(path);
    }

    public static void ReplaceKeepingReadOnly(string temp, string target)
    {
        var attrs = ClearReadOnly(target);
        try { File.Move(temp, target, overwrite: true); }
        catch
        {
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                try { File.SetAttributes(target, attrs); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(target, attrs);
    }

    internal static void WriteNew(string tmp, byte[] bytes, bool flushToDisk = false)
    {
        using var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        fs.Write(bytes);
        if (flushToDisk) fs.Flush(flushToDisk: true);
    }
}
