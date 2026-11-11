using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace Provenance.Common.Otp;

public enum BitwardenLogin { LoggedIn, AlreadyLoggedIn, NotLoggedIn, ServerRefused, NoAnswer, NoTool }

public static class Bitwarden
{
    public const string LoginText = "Bitwarden asks for your email, master password and two-step login code in a window of its own.";

    // this run's unlock, memory only
    private static string? _session;

    internal static async Task<OtpFetchResult> FetchAsync(string item, string? masterPassword, CancellationToken ct)
    {
        if (OtpSources.StartsWithDash(item)) return OtpSources.Problem("item starts with a dash", OtpSources.DashText, OtpFix.TypeInstead);
        using var found = await Task.Run(() => ToolFinder.Find(ToolFacts.Bitwarden), ct);
        if (found.Path is not { } bw) return OtpSources.ToolProblem(ToolFacts.Bitwarden, found);
        try
        {
            var env = new Dictionary<string, string>();
            if (masterPassword is { Length: > 0 })
            {
                // the password only ever goes in that one process's environment, never on a command line
                var u = await ToolProcess.RunAsync(bw, ["unlock", "--passwordenv", "PROVENANCE_BW_PASSWORD", "--raw", "--nointeraction"],
                                                   new Dictionary<string, string> { ["PROVENANCE_BW_PASSWORD"] = masterPassword },
                                                   TimeSpan.FromSeconds(60), ct);
                if (u.Exit != 0 || u.Out.Trim().Length == 0)
                    return OtpSources.Problem($"didn't unlock (exit {u.Exit})", "Bitwarden didn't unlock.", OtpFix.MasterPassword);
                _session = u.Out.Trim();
            }
            if (_session is { } session) env["BW_SESSION"] = session;
            var status = await StatusAsync(bw, env, ct);
            if (status == "unauthenticated")
                return OtpSources.Problem("not logged in", "Bitwarden needs logging in. Press Accounts and edit this account to log in to it.", OtpFix.TypeInstead);
            if (status == "locked")
            {
                _session = null;
                return OtpSources.Problem("locked", "Bitwarden is locked.", OtpFix.MasterPassword);
            }
            if (status != "unlocked") return OtpSources.Problem("no status", "Bitwarden didn't answer.");
            var r = await ToolProcess.RunAsync(bw, ["get", "totp", item, "--nointeraction"], env, TimeSpan.FromSeconds(30), ct);
            // bw never syncs by itself, so an item added since it logged in isn't there yet
            if (r.Exit != 0 && CleanError(r.Err) == "Not found.")
            {
                var sync = await ToolProcess.RunAsync(bw, ["sync", "--nointeraction"], env, TimeSpan.FromSeconds(60), ct);
                Log.Information("Bitwarden item not found, synced (exit {Exit}) and asked again", sync.Exit);
                r = await ToolProcess.RunAsync(bw, ["get", "totp", item, "--nointeraction"], env, TimeSpan.FromSeconds(30), ct);
            }
            return OtpSources.CodeFrom(r, err => ProblemFor(err, item));
        }
        finally { ToolInstaller.UpdateInBackground(ToolFacts.Bitwarden); }
    }

    internal static (string Outcome, string Text, OtpFix Fix) ProblemFor(string err, string item)
    {
        bool Says(string s) => err.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Says("Premium status is required"))
            return ("premium needed", "Bitwarden only gives one-time passwords to Premium accounts.", OtpFix.TypeInstead);
        if (Says("More than one result was found"))
            return ("several items", $"More than one Bitwarden item is called \"{item}\".", OtpFix.TypeInstead);
        if (Says("No TOTP available"))
            return ("no authenticator key", $"The Bitwarden item \"{item}\" has no authenticator key.", OtpFix.TypeInstead);
        if (CleanError(err) == "Not found.")
            return ("not an item", $"Bitwarden has no item called \"{item}\".", OtpFix.TypeInstead);
        return ("unrecognised error", "Bitwarden didn't give the code.", OtpFix.TryAgain);
    }

    private static async Task<string> StatusAsync(string bw, IDictionary<string, string>? env, CancellationToken ct)
    {
        var s = await ToolProcess.RunAsync(bw, ["status", "--nointeraction"], env, TimeSpan.FromSeconds(30), ct);
        try { return JsonDocument.Parse(s.Out).RootElement.GetProperty("status").GetString() ?? ""; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return ""; }
    }

    // "bitwarden.com" puts bw back on its own default. Null for a self-hosted address that isn't a plain https URL
    public static string? Server(string picked, string typed)
    {
        if (picked.Length > 0) return picked;
        if (!Uri.TryCreate(typed.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.Host.Length == 0
            || u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0)
            return null;
        return u.AbsoluteUri.TrimEnd('/');
    }

    private static readonly object LoginGate = new();
    private static Task<BitwardenLogin>? _login;
    private static Process? _loginWindow;

    // the login that's still going, if there is one. Edit account watches it rather than starting another
    public static Task<BitwardenLogin>? LoginRunning
    {
        get { lock (LoginGate) return _login is { IsCompleted: false } running ? running : null; }
    }

    internal static void SetLoginForTest(Task<BitwardenLogin>? login)
    {
        lock (LoginGate) _login = login;
    }

    // one window at a time. Closing Edit account only stops watching it, the window's the user's to finish
    public static Task<BitwardenLogin> LogInAsync(string server)
    {
        lock (LoginGate)
        {
            if (_login is { IsCompleted: false } running) return running;
            return _login = Task.Run(() => RunLoginAsync(server));
        }
    }

    // bw asks for everything in its own window. --quiet so the session key never shows, and it's locked when the
    // window closes
    private static async Task<BitwardenLogin> RunLoginAsync(string server)
    {
        using var found = await Task.Run(() => ToolFinder.Find(ToolFacts.Bitwarden));
        if (found.Path is not { } bw) return BitwardenLogin.NoTool;
        var before = await StatusAsync(bw, null, CancellationToken.None);
        if (before is "locked" or "unlocked")
        {
            Log.Information("Bitwarden login: already logged in");
            return BitwardenLogin.AlreadyLoggedIn;
        }
        if (before != "unauthenticated")
        {
            Log.Information("Bitwarden login: no status from bw");
            return BitwardenLogin.NoAnswer;
        }
        var config = await ToolProcess.RunAsync(bw, ["config", "server", server, "--nointeraction"], null, TimeSpan.FromSeconds(30), CancellationToken.None);
        if (config.Exit != 0)
        {
            Log.Information("Bitwarden login: the server wasn't taken (exit {Exit})", config.Exit);
            return BitwardenLogin.ServerRefused;
        }
        var psi = ToolProcess.StartInfo(bw, ["login", "--quiet"], hidden: false);
        try
        {
            using var window = Process.Start(psi) ?? throw new InvalidOperationException("bw did not start.");
            lock (LoginGate) _loginWindow = window;
            await window.WaitForExitAsync();
        }
        finally
        {
            lock (LoginGate) _loginWindow = null;
            if (await LockAsync(bw, null, TimeSpan.FromSeconds(30), CancellationToken.None) is { } exit)
                Log.Information("Bitwarden locked (exit {Exit})", exit);
        }
        var after = await StatusAsync(bw, null, CancellationToken.None);
        var result = after is "locked" or "unlocked" ? BitwardenLogin.LoggedIn
                   : after == "unauthenticated" ? BitwardenLogin.NotLoggedIn
                   : BitwardenLogin.NoAnswer;
        Log.Information("Bitwarden login: {Result}", result);
        return result;
    }

    // the exit code, or null when it couldn't be run. Never throws
    private static async Task<int?> LockAsync(string bw, string? session, TimeSpan limit, CancellationToken ct)
    {
        try
        {
            var env = session is null ? null : new Dictionary<string, string> { ["BW_SESSION"] = session };
            return (await ToolProcess.RunAsync(bw, ["lock", "--nointeraction"], env, limit, ct)).Exit;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Locking Bitwarden failed");
            return null;
        }
    }

    // at exit a login window still open is closed (so none finishes after Provenance), then it's locked and the
    // session's forgotten either way. Never throws
    public static async Task LockAtExitAsync()
    {
        Process? window;
        lock (LoginGate) window = _loginWindow;
        try { window?.Kill(entireProcessTree: true); }
        catch (Exception ex) { Log.Debug(ex, "Closing Bitwarden's login window failed"); }
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null && window is null) return;
        try
        {
            using var found = ToolFinder.Find(ToolFacts.Bitwarden);
            if (found.Path is not { } bw) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await LockAsync(bw, session, TimeSpan.FromSeconds(5), cts.Token);
        }
        catch (Exception ex) { Log.Warning(ex, "Locking Bitwarden failed"); }
    }

    // "[ERROR] 2026/10/19 20:00:00 authorization prompt dismissed" -> "authorization prompt dismissed". bw's colours go too
    private static string CleanError(string err)
    {
        var line = Regex.Replace(err, @"\x1b\[[0-9;]*m", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        line = Regex.Replace(line, @"^\[ERROR\]\s+\S+\s+\S+\s+", "");
        return line;
    }
}
