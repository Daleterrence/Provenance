using System.Text;
using Provenance.Common.IO;

namespace Provenance.Common.Feeds;

// the last answer the worker gave for each address, so a worker that can't be reached still has something to show
public static class FeedCache
{
    public sealed record Entry(string Body, string? ETag, string? LastModified, DateTime SavedAt);

    internal static string? RootOverride { get; set; }

    internal static string? Folder(string name) =>
        (RootOverride ?? AppPaths.UserData) is { } root ? Path.Combine(root, "cache", name) : null;

    private static string? CachePath(string url) =>
        Folder("feeds") is { } dir ? Path.Combine(dir, FileHash.Sha256(Encoding.UTF8.GetBytes(url)) + ".cache") : null;

    // "ETag: ..." and "Last-Modified: ..." lines, a blank line, then the body
    public static async Task<Entry?> ReadAsync(string url, CancellationToken ct)
    {
        try
        {
            if (CachePath(url) is not { } path || !File.Exists(path)) return null;
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var split = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (split < 0) return null;
            var head = text[..split].Split('\n');
            return new Entry(text[(split + 2)..], Header(head, "ETag:"), Header(head, "Last-Modified:"),
                             File.GetLastWriteTimeUtc(path));
        }
        catch { return null; }

        static string? Header(string[] lines, string name)
        {
            var line = lines.LastOrDefault(l => l.StartsWith(name, StringComparison.Ordinal));
            return line?[(line.IndexOf(':') + 1)..].Trim() is { Length: > 0 } v ? v : null;
        }
    }

    // a 304 means the saved copy is still current, so it counts as checked now
    public static void Touch(string url)
    {
        try
        {
            if (CachePath(url) is { } path && File.Exists(path)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch { }
    }

    public static void Write(string url, string body, string? etag, string? lastModified)
    {
        try
        {
            if (CachePath(url) is not { } path) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Write(path, $"ETag: {etag}\nLast-Modified: {lastModified}\n\n{body}");
        }
        catch { }
    }
}
