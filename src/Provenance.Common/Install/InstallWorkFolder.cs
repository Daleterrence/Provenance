using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Serilog;
using static Provenance.Common.IO.FileHandles;
using static Provenance.Common.Security.LockedFolder;

namespace Provenance.Common.Install;

public static class InstallWorkFolder
{
    internal static Action<string>? HeldForTest;

    public sealed class FolderRefusedException(string message) : IOException(message);

    public sealed class WorkFolder(string path, SafeFileHandle chosen, SafeFileHandle work) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            work.Dispose();
            chosen.Dispose();
        }
    }

    public static WorkFolder? HoldWorkFolder(string chosen, IReadOnlyCollection<string> ownNames, Func<string, string?> problem)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(chosen));
        var missing = new Stack<string>();
        var existing = full;
        for (; !Directory.Exists(existing); existing = Path.GetDirectoryName(existing) ?? throw new FolderRefusedException($"{full} has no drive"))
            missing.Push(existing);
        if (problem(existing) is { } before) throw new FolderRefusedException(before);
        while (missing.TryPop(out var dir)) CreateLocked(dir, Intended(usersMayRead: true));
        // read sharing only, so nothing with it open to write or delete passes, and nothing opens it that way later
        var held = OpenLinkItself(full, ListDirectory | ReadControl | ReadAttributes | Synchronize, ShareRead);
        if (held.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            held.Dispose();
            if (error == 32)
                throw Provenance.Common.Shown.Mark(new IOException($"Another program has {full} open. Close it, then press Try again."));
            throw new FolderRefusedException($"{full} couldn't be opened ({error})");
        }
        bool keep = false;
        try
        {
            HeldForTest?.Invoke(full);
            var seen = Look(held);
            if (!seen.Read || !seen.IsDir || seen.IsLink) throw new FolderRefusedException($"{full} is a link or not a folder");
            if (RealPath(held) is not { } real || !string.Equals(real, full, StringComparison.OrdinalIgnoreCase))
                throw new FolderRefusedException($"{full} is really {RealPath(held) ?? "somewhere it can't tell"}");
            if (problem(full) is { } why) throw new FolderRefusedException(why);
            foreach (var name in Enumerable.Range(1, 9).Select(i => i == 1 ? "ffxi-download" : $"ffxi-download-{i}"))
            {
                var path = Path.Combine(full, name);
                if (HoldOwn(held, path, name, ownNames) is not { } work)
                {
                    Log.Warning("{Folder} was not made by Provenance; it is left alone", path);
                    continue;
                }
                keep = true;
                return new WorkFolder(path, held, work);
            }
            return null;
        }
        finally
        {
            if (!keep) held.Dispose();
        }
    }

    private static string? RealPath(SafeFileHandle h) =>
        FinalPath(h, allowUnc: false) is { } path ? Path.TrimEndingDirectorySeparator(path) : null;
}
