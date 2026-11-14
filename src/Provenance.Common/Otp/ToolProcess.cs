using System.Diagnostics;
using Provenance.Common.Security;

namespace Provenance.Common.Otp;

internal static class ToolProcess
{
    internal static string? HomeRootForTest;

    // the tools' own profile, per Windows user and admin-only, so nothing they write or unpack to load is the user's
    internal static string Home
    {
        get
        {
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            var home = Path.Combine(HomeRootForTest ?? ProtectedFolders.RootSubDir("home"), me.User?.Value ?? "user");
            foreach (var sub in new[] { "", @"AppData\Roaming", @"AppData\Local", "Temp" }) Directory.CreateDirectory(Path.Combine(home, sub));
            return home;
        }
    }

    internal sealed record RunResult(int Exit, string Out, string Err)
    {
        private bool PrintMembers(System.Text.StringBuilder builder)
        {
            builder.Append("Out = ***");
            return true;
        }
    }

    // its own folder, never whatever folder the elevated app happens to be in. Hidden means no window and every stream redirected
    internal static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args, bool hidden = true)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
        if (hidden)
        {
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        }
        LockedEnvironment(psi.Environment);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    // stdin's closed straight away, so it can never sit waiting for typing
    internal static async Task<RunResult> RunAsync(string exe, IEnumerable<string> args, IDictionary<string, string>? env,
                                                  TimeSpan timeout, CancellationToken ct)
    {
        var psi = StartInfo(exe, args);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(Path.GetFileName(exe) + " did not start.");
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync(ct);
        var e = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }   // already gone
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileNameWithoutExtension(exe)} didn't answer within {timeout.TotalSeconds:0} seconds.");
        }
        return new RunResult(p.ExitCode, await o, await e);
    }

    // built from nothing: Windows' own folders on PATH, the profile in Provenance's locked home, nothing else the user's set
    internal static void LockedEnvironment(IDictionary<string, string?> env)
    {
        var windows = AppPaths.Windows;
        var system = Environment.SystemDirectory;
        var profile = Home;
        env.Clear();
        env["SystemRoot"] = env["windir"] = windows;
        env["SystemDrive"] = Path.GetPathRoot(windows)?.TrimEnd('\\') ?? "C:";
        env["ComSpec"] = Path.Combine(system, "cmd.exe");
        env["PATH"] = string.Join(';', system, windows, Path.Combine(system, "Wbem"));
        env["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        env["USERPROFILE"] = profile;
        env["HOMEDRIVE"] = Path.GetPathRoot(profile)?.TrimEnd('\\') ?? "";
        env["HOMEPATH"] = profile[(env["HOMEDRIVE"]!.Length)..];
        env["APPDATA"] = Path.Combine(profile, "AppData", "Roaming");
        env["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local");
        env["ProgramData"] = AppPaths.CommonAppData;
        env["TEMP"] = env["TMP"] = Path.Combine(profile, "Temp");
        env["USERNAME"] = Environment.UserName;
        env["USERDOMAIN"] = Environment.UserDomainName;
        env["COMPUTERNAME"] = Environment.MachineName;
        env["NoDefaultCurrentDirectoryInExePath"] = "1";
    }
}
