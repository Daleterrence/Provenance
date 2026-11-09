using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Provenance.Common.Security;
using Serilog;

namespace Provenance.Common.Otp;

// finds the tool's own copy and checks it before every use. Provenance only ever runs the copy it put in its own folder
public static class ToolFinder
{
    private static readonly Regex VersionText = new(@"^[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}$");

    private static string? _dirForTest;
    internal static Func<string, ToolFind>? FindForTest;

    internal static void SetDirForTest(string? dir) => _dirForTest = dir;

    // C:\ProgramData\Provenance\tools, only Administrators and SYSTEM can change it. Users may read, the 1Password app
    // checks op.exe's signature from the user's side
    internal static string Dir => _dirForTest ?? ProtectedFolders.RootSubDir("tools", usersMayRead: true);

    public static ToolFind Find(string tool)
    {
        if (FindForTest is { } fake) return fake(tool);
        var own = Path.Combine(Dir, tool + ".exe");
        return Find(tool, File.Exists(own) && PathSafety.OnLocalFixedDrive(own) ? own : null, CheckToRun);
    }

    internal static Verdict CheckToRun(SafeFileHandle file, string path, string tool)
    {
        var verdict = ToolSignature.Check(file, path, tool, online: false);
        if (verdict != Verdict.Genuine) return verdict;
        var version = ToolNote.NotedVersion(Path.GetDirectoryName(path)!, tool) ?? VersionOf(path);
        if (version is null) return Verdict.CantCheck;
        if (version >= ToolFacts.Oldest(tool)) return Verdict.Genuine;
        Log.Warning("{Exe} in {Folder} is version {Version}, older than Provenance can use", tool + ".exe", Path.GetDirectoryName(path), version);
        return Verdict.TooOld;
    }

    // only Provenance's own copy, the ProtectedFolders lock on its folder is what keeps it admin-only
    internal static ToolFind Find(string tool, string? own, Func<SafeFileHandle, string, string, Verdict> check)
    {
        if (own is null || ToolSignature.Open(own) is not { } ownFile) return new ToolFind(FindState.NotInstalled);
        Verdict verdict;
        try { verdict = check(ownFile, own, tool); }
        catch { ownFile.Dispose(); throw; }
        if (verdict == Verdict.Genuine) return new ToolFind(own, ownFile);
        ownFile.Dispose();
        if (verdict == Verdict.NotGenuine) DeleteOwn(own, tool, check);
        return new ToolFind(verdict switch { Verdict.CantCheck => FindState.CantCheck, Verdict.TooOld => FindState.TooOld, _ => FindState.NotInstalled });
    }

    // checked again with the lock held, another window may have just put a good copy in
    private static void DeleteOwn(string own, string tool, Func<SafeFileHandle, string, string, Verdict> check)
    {
        try
        {
            using var held = AdminLock.TryAcquire(ToolFacts.LockName(tool), TimeSpan.Zero);
            if (held is null) return;
            using (var file = ToolSignature.Open(own))
                if (file is null || check(file, own, tool) != Verdict.NotGenuine) return;
            Log.Warning("Provenance's own {Exe} isn't signed by its maker, so it's deleted", tool + ".exe");
            TryDelete(own);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Debug(ex, "Could not delete {File}", own);
        }
    }

    // what the checked file says it is, op and bw have no version resource of their own. Kept per file for the run
    private static readonly Dictionary<string, (long Size, DateTime Written, Version? Version)> Versions = new(StringComparer.OrdinalIgnoreCase);

    internal static Version? VersionOf(string path)
    {
        var info = new FileInfo(path);
        lock (Versions)
            if (Versions.TryGetValue(path, out var seen) && seen.Size == info.Length && seen.Written == info.LastWriteTimeUtc) return seen.Version;
        if (ParseVersion(AskVersion(path)) is not { } version) return null;
        lock (Versions) Versions[path] = (info.Length, info.LastWriteTimeUtc, version);
        return version;
    }

    internal static Version? ParseVersion(string? line) => line is not null && VersionText.IsMatch(line) ? Version.Parse(line) : null;

    // no async in here, so it can't wait on a thread it's blocking. A version's one line, 4 KB is plenty
    private static string? AskVersion(string path)
    {
        try
        {
            var psi = ToolProcess.StartInfo(path, ["--version"]);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException(Path.GetFileName(path) + " did not start.");
            p.StandardInput.Close();
            p.ErrorDataReceived += static (_, _) => { };
            p.BeginErrorReadLine();
            var read = Task.Run(() =>
            {
                var buf = new char[4096];
                int n = 0;
                for (int r; n < buf.Length && (r = p.StandardOutput.Read(buf, n, buf.Length - n)) > 0;) n += r;
                return new string(buf, 0, n);
            });
            if (!p.WaitForExit(30_000) || !read.Wait(5_000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                Log.Information("{File} didn't give its version in time", Path.GetFileName(path));
                return null;
            }
            if (p.ExitCode != 0) return null;
            return read.Result.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Information(ex, "Asking {File} for its version failed", Path.GetFileName(path));
            return null;
        }
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug(ex, "Could not delete {File}", path); }
    }
}
