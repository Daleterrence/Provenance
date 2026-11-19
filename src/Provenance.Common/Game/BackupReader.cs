using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Provenance.Common.IO;
using Provenance.Common.Win32;

namespace Provenance.Common.Game;

internal static class BackupReader
{
    private const int Tries = 4, SettleRounds = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);
    internal static TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    internal static Action<string>? AfterReadForTest;
    internal static Action? BeforeSettleForTest;

    internal readonly record struct Stat(long Size, long Written);

    internal sealed record Found(string Entry, string Path, string Folder, bool ZeroIsTorn, Stat Stat);

    internal sealed class Taken(Found file, byte[] data, Stat stat, long readAt)
    {
        public Found File = file;
        public byte[] Data = data;
        public Stat Stat = stat;
        public long ReadAt = readAt;
    }

    // an empty character file is one caught mid-write, the game always writes something
    internal static Taken? ReadSteady(Found f)
    {
        for (int i = 0; i < Tries; i++)
        {
            if (i > 0) Thread.Sleep(RetryDelay);
            using var h = OpenChecked(f.Path, f.Folder, data: true, out var before);
            if (before.Size > GameSettingsBackup.MaxFile) throw new IOException($"it's {before.Size} bytes, far bigger than any game settings file");
            var data = ReadAll(h, before.Size);
            AfterReadForTest?.Invoke(f.Path);
            var after = StatOf(Info(h));
            if (data is not null && after == before && !(f.ZeroIsTorn && before.Size == 0)) return new(f, data, before, Environment.TickCount64);
        }
        return null;
    }

    // the C runtime writes a big file in two goes, and between them it sits at the first part's size
    internal static void SettleDown(List<Taken> taken, List<Found> torn, List<string> leftOut)
    {
        BeforeSettleForTest?.Invoke();
        for (int round = 0; taken.Count > 0; round++)
        {
            var wait = Settle - TimeSpan.FromMilliseconds(Environment.TickCount64 - taken.Max(t => t.ReadAt));
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            var moved = taken.Where(t => !StillAt(t)).ToList();
            if (moved.Count == 0) return;
            foreach (var t in moved)
            {
                taken.Remove(t);
                if (round == SettleRounds)
                {
                    torn.Add(t.File);
                    continue;
                }
                try
                {
                    if (ReadSteady(t.File) is { } again) taken.Add(again);
                    else torn.Add(t.File);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { leftOut.Add($"{t.File.Entry}: {e.Message}"); }
            }
            if (round == SettleRounds) return;
        }
    }

    private static bool StillAt(Taken t)
    {
        try
        {
            using var h = OpenChecked(t.File.Path, t.File.Folder, data: false, out var now);
            return now == t.Stat;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static byte[]? ReadAll(SafeFileHandle h, long size)
    {
        var data = new byte[size + 1];
        int got = 0;
        while (got < data.Length)
        {
            int n = RandomAccess.Read(h, data.AsSpan(got), got);
            if (n == 0) break;
            got += n;
        }
        return got == size ? data[..got] : null;
    }

    // a link, hard link or moved file would make an elevated copy read what it shouldn't, so all three are refused
    internal static SafeFileHandle OpenChecked(string path, string folder, bool data, out Stat stat)
    {
        var h = FileHandles.Open(FileHandles.LongPath(Path.GetFullPath(path)), data ? FileHandles.GenericRead : FileHandles.ReadAttributes,
                                 FileHandles.ShareAll, FileHandles.OpenReparsePoint);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw err == 5 ? new UnauthorizedAccessException("Windows didn't let Provenance read it") : new IOException($"it couldn't be opened (error {err})");
        }
        try
        {
            var info = Info(h);
            if (((FileAttributes)info.FileAttributes).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("it's a link");
            if (((FileAttributes)info.FileAttributes).HasFlag(FileAttributes.Directory)) throw new IOException("it's a folder");
            if (info.NumberOfLinks != 1) throw new IOException("it has other names (a hard link)");
            if (FileHandles.FinalPath(h, allowUnc: true) is not { } where || !string.Equals(Path.GetDirectoryName(where), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase))
                throw new IOException("it isn't in the folder it was found in");
            stat = StatOf(info);
            return h;
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    private static Stat StatOf(ByHandleFileInformation info) => new(((long)info.FileSizeHigh << 32) | info.FileSizeLow, info.LastWriteTime);

    private static ByHandleFileInformation Info(SafeFileHandle h) =>
        FileHandles.TryInfo(h, out var info) ? info : throw new IOException($"its details couldn't be read (error {Marshal.GetLastPInvokeError()})");
}
