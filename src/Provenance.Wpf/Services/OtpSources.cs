using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using Provenance.Common.Credentials;
using Provenance.Common.Locks;
using Provenance.Common.Otp;

namespace Provenance.Wpf.Services;

// Problem is user text. NeedsMasterPassword means Bitwarden's locked, so ask for its master password and go again
public sealed record OtpFetchResult(string? Code, string? Problem, bool NeedsMasterPassword = false);

// The code from a password manager that's unlocked on this PC, read at sign-in, passed on once and never stored
public static class OtpSources
{
    public const string Bitwarden = "bitwarden", OnePassword = "1password", KeePassXC = "keepassxc";
    private const string KeePassXcKey = "keepassxc-association";
    private const string NoAnswer = "KeePassXC didn't answer.";
    private static readonly Regex SixDigits = new(@"^\d{6}$");

    // this run's Bitwarden unlock, memory only
    private static string? _bitwardenSession;

    public static string Name(string source) => source switch
    {
        Bitwarden => "Bitwarden",
        OnePassword => "1Password",
        KeePassXC => "KeePassXC",
        _ => "",
    };

    public static (string Hint, string Help) Describe(string source) => source switch
    {
        Bitwarden => ("Item name or ID in Bitwarden",
                      "Provenance gets the code from the Bitwarden command-line tool (bw). If your vault's locked, Provenance asks for your Bitwarden master password once, and the vault only stays unlocked until Provenance closes."),
        OnePassword => ("Item name or ID in 1Password",
                        "Provenance gets the code from the 1Password command-line tool (op). Turn on \"Integrate with 1Password CLI\" in the 1Password app, and it'll ask you to confirm with Windows Hello or your password."),
        KeePassXC => ("Entry URL in KeePassXC",
                      "Provenance gets the code from KeePassXC through its browser integration, using the URL saved in the entry. Turn on browser integration in KeePassXC. The first time, KeePassXC asks you to allow Provenance."),
        _ => ("", ""),
    };

    // never throws, a problem comes back as text
    public static async Task<OtpFetchResult> FetchAsync(string source, string item, string seaId, string? bitwardenPassword, CancellationToken ct)
    {
        try
        {
            return source switch
            {
                OnePassword => await FromOnePasswordAsync(item, ct),
                Bitwarden => await FromBitwardenAsync(item, bitwardenPassword, ct),
                KeePassXC => await FromKeePassXcAsync(item, seaId, ct),
                _ => new OtpFetchResult(null, "This account doesn't have a password manager chosen."),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new OtpFetchResult(null, "Cancelled."); }
        catch (TimeoutException ex) { return new OtpFetchResult(null, ex.Message); }
        catch (CredentialKeyUnavailableException ex) { return new OtpFetchResult(null, ex.Message); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading the one-time password from {Source} failed", source);
            return new OtpFetchResult(null, $"{Name(source)} didn't give the code.");
        }
    }

    private static OtpFetchResult CodeFrom(RunResult r, string name, string tool)
    {
        var code = r.Out.Trim();
        if (r.Exit == 0 && SixDigits.IsMatch(code)) return new OtpFetchResult(code, null);
        return new OtpFetchResult(null, $"{name} didn't give the code: " + CleanError(r.Err, tool));
    }

    private static async Task<OtpFetchResult> FromOnePasswordAsync(string item, CancellationToken ct)
    {
        if (FindTool("op") is not { } op)
            return new OtpFetchResult(null, "Couldn't find the 1Password command-line tool (op). Install it (winget install AgileBits.1Password.CLI) and turn on \"Integrate with 1Password CLI\" in the 1Password app.");
        if (Unprotected(op, "winget install --scope machine AgileBits.1Password.CLI") is { } problem) return new OtpFetchResult(null, problem);
        // the app confirms with Windows Hello or its password, hence the long wait
        var r = await RunAsync(op, ["item", "get", item, "--otp"],
                               new Dictionary<string, string> { ["OP_BIOMETRIC_UNLOCK_ENABLED"] = "true" }, TimeSpan.FromSeconds(90), ct);
        return CodeFrom(r, "1Password", "op");
    }

    private static async Task<OtpFetchResult> FromBitwardenAsync(string item, string? masterPassword, CancellationToken ct)
    {
        if (FindTool("bw", allowCmd: true) is not { } bw)
            return new OtpFetchResult(null, "Couldn't find the Bitwarden command-line tool (bw). Install it (winget install Bitwarden.CLI) and log in once with \"bw login\".");
        if (Unprotected(bw, "winget install --scope machine Bitwarden.CLI") is { } problem) return new OtpFetchResult(null, problem);
        bool shim = IsCmd(bw);
        if (shim && BitwardenCmdProblem(bw) is { } cmdProblem) return new OtpFetchResult(null, cmdProblem);
        // cmd.exe parses the line again, so only plain IDs and names get through
        if (shim && !Regex.IsMatch(item, @"^[A-Za-z0-9_.@-]+$"))
            return new OtpFetchResult(null, "Use the Bitwarden item's ID here. This copy of the Bitwarden command-line tool can't look up names with spaces or symbols in them.");
        var env = new Dictionary<string, string>();
        if (masterPassword is { Length: > 0 })
        {
            // the password only ever goes in that one process's environment, never on a command line
            var u = await RunAsync(bw, ["unlock", "--passwordenv", "PROVENANCE_BW_PASSWORD", "--raw", "--nointeraction"],
                                   new Dictionary<string, string> { ["PROVENANCE_BW_PASSWORD"] = masterPassword }, TimeSpan.FromSeconds(60), ct);
            if (u.Exit != 0 || u.Out.Trim().Length == 0)
                return new OtpFetchResult(null, "Bitwarden didn't unlock: " + CleanError(u.Err, "bw"), NeedsMasterPassword: true);
            _bitwardenSession = u.Out.Trim();
        }
        if (_bitwardenSession is { } session) env["BW_SESSION"] = session;
        var s = await RunAsync(bw, ["status", "--nointeraction"], env, TimeSpan.FromSeconds(30), ct);
        string status = "";
        try { status = JsonDocument.Parse(s.Out).RootElement.GetProperty("status").GetString() ?? ""; } catch { }
        if (status == "unauthenticated")
            return new OtpFetchResult(null, "The Bitwarden command-line tool isn't logged in. Run \"bw login\" once, then try again.");
        if (status == "locked")
        {
            _bitwardenSession = null;
            return new OtpFetchResult(null, "Bitwarden is locked. Enter your Bitwarden master password to unlock it until Provenance closes.", NeedsMasterPassword: true);
        }
        var r = await RunAsync(bw, ["get", "totp", item, "--nointeraction"], env, TimeSpan.FromSeconds(30), ct);
        return CodeFrom(r, "Bitwarden", "bw");
    }

    private static bool IsCmd(string tool) => tool.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);

    // npm's bw.cmd starts node.exe from its own folder, as admin with the master password in its environment, so
    // that node.exe has to be admin-only
    internal static string? BitwardenCmdProblem(string cmdPath) => BitwardenCmdProblem(cmdPath, PathSafety.IsAdminOnly);

    internal static string? BitwardenCmdProblem(string cmdPath, Func<string, bool> isAdminOnly)
    {
        try
        {
            var node = Path.Combine(Path.GetDirectoryName(cmdPath) ?? "", "node.exe");
            if (File.Exists(node) && isAdminOnly(node)) return null;
        }
        catch { }
        return "Provenance can't run this Bitwarden CLI safely. Install the standalone bw.exe from Bitwarden.";
    }

    // the session's forgotten even if the lock gives up after 5 secs. Never throws
    public static async Task LockBitwardenAsync()
    {
        var session = Interlocked.Exchange(ref _bitwardenSession, null);
        if (session is null) return;
        try
        {
            var bw = FindTool("bw", allowCmd: true);
            if (bw is null || Unprotected(bw, "") is not null) return;
            if (IsCmd(bw) && BitwardenCmdProblem(bw) is not null) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await RunAsync(bw, ["lock", "--nointeraction"], new Dictionary<string, string> { ["BW_SESSION"] = session },
                           TimeSpan.FromSeconds(5), cts.Token);
        }
        catch (Exception ex) { Log.Warning(ex, "Locking Bitwarden failed"); }
    }

    private static StoredCredential? ReadAssociation(WindowsCredentialStore store)
    {
        try { return store.Read(KeePassXcKey); }
        catch (CredentialUnreadableException) { return null; }
    }

    // wrapped with the saved accounts' key, and opened on a worker so the lock wait never lands on the UI thread
    private static readonly WrappedStoreCache<Task<WindowsCredentialStore>> AssociationStore =
        new(() => Task.Run(() => new WindowsCredentialStore(wrapKey: CredentialKey.Open())),
            keep: static t => t is { IsFaulted: false, IsCanceled: false });

    internal static Task<WindowsCredentialStore> AssociationStoreAsync() => AssociationStore.Get();

    public static Task OpenKeyInBackground() =>
        AssociationStoreAsync().ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                                             TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    internal static void ResetAssociationStoreForTest() => AssociationStore.Reset();

    private static async Task<OtpFetchResult> FromKeePassXcAsync(string url, string seaId, CancellationToken ct)
    {
        var proxy = FindKeePassXcProxy();
        if (proxy is null)
            return new OtpFetchResult(null, "Couldn't find KeePassXC. Install KeePassXC and turn on browser integration in its settings.");
        // a key replaced while the app runs means pairing again, never saving with the old key. Before the proxy starts
        await Task.Run(CredentialKey.CheckFile, ct);
        var store = await AssociationStoreAsync().WaitAsync(ct);
        var psi = new ProcessStartInfo(proxy)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            // same as below, its own folder and never wherever the elevated app is sat
            WorkingDirectory = Path.GetDirectoryName(proxy) ?? "",
        };
        psi.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("keepassxc-proxy did not start.");
        _ = p.StandardError.ReadToEndAsync(ct);
        void Stop() { try { if (!p.HasExited) p.Kill(); } catch { } }

        // reads on the proxy's pipe ignore cancellation, so the limit (or a cancel) kills the proxy instead
        async Task<T> Timed<T>(Func<CancellationToken, Task<T>> work, TimeSpan limit, string message = NoAnswer)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(limit);
            using var reg = cts.Token.Register(Stop);
            try { return await work(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException(message); }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                throw new TimeoutException(message);
            }
        }
        Task Timed0(Func<CancellationToken, Task> work, TimeSpan limit, string message) =>
            Timed(async t => { await work(t); return true; }, limit, message);

        try
        {
            using var client = new KeePassXcClient(p.StandardInput.BaseStream, p.StandardOutput.BaseStream);
            Task<bool> Paired(string pairId, byte[] pairKey) =>
                Timed(t => client.TestAssociateAsync(pairId, pairKey, t), TimeSpan.FromSeconds(15));
            await Timed0(t => client.ExchangeKeysAsync(t), TimeSpan.FromSeconds(10),
                         "KeePassXC didn't answer. Start KeePassXC and turn on browser integration in its settings.");
            var saved = ReadAssociation(store);
            string? id = saved?.UserName;
            byte[]? idKey = null;
            if (saved is { } c)
            {
                var buf = new byte[64];
                if (Convert.TryFromBase64String(c.Secret, buf, out int n) && n == 32) idKey = buf[..32];   // else pair again
            }
            if (id is null || idKey is null || !await Paired(id, idKey))
            {
                // first use, so KeePassXC asks to allow the app. One copy pairs at a time, after 2 mins it pairs anyway
                IDisposable? pairing = null;
                try
                {
                    // a file lock, so letting go on another thread is fine. Per Windows user, like the association
                    pairing = await Task.Run(() => AdminLock.TryAcquire(AdminLock.PerUser("keepassxc-pairing"), TimeSpan.FromMinutes(2), ct), ct);
                    // another copy may have paired while this one waited
                    if (ReadAssociation(store) is { } again && again.Secret != saved?.Secret &&
                        Convert.TryFromBase64String(again.Secret, new byte[64], out _) &&
                        await Paired(again.UserName, Convert.FromBase64String(again.Secret)))
                    {
                        (id, idKey) = (again.UserName, Convert.FromBase64String(again.Secret));
                    }
                    else
                    {
                        idKey = KeePassXcClient.NewIdKey();
                        var newKey = idKey;
                        id = await Timed(t => client.AssociateAsync(newKey, t), TimeSpan.FromMinutes(2),
                                         "KeePassXC didn't allow Provenance in time. Try again, and choose to allow it in KeePassXC.");
                        store.Save(KeePassXcKey, id, Convert.ToBase64String(idKey));
                    }
                }
                finally { pairing?.Dispose(); }
                if (!await Paired(id, idKey))
                    return new OtpFetchResult(null, "KeePassXC didn't accept the connection.");
            }
            var (useId, useKey) = (id, idKey);
            Task<IReadOnlyList<KeePassXcEntry>> Logins(bool unlock, int secs) =>
                Timed(t => client.GetLoginsAsync(url, useId, useKey, unlock, t), TimeSpan.FromSeconds(secs));
            IReadOnlyList<KeePassXcEntry> entries;
            try { entries = await Logins(false, 60); }
            catch (KeePassXcException e) when (e.Code == KeePassXcException.DatabaseLocked)
            {
                // KeePassXC shows its unlock dialog, then says when the database is open
                try { await Logins(true, 15); }
                catch (KeePassXcException) { }
                await Timed0(t => client.WaitForUnlockAsync(t), TimeSpan.FromMinutes(2), "KeePassXC is still locked.");
                entries = await Logins(false, 60);
            }
            var (picked, pickProblem) = PickKeePassXcEntry(entries, seaId, url);
            if (picked is null) return new OtpFetchResult(null, pickProblem);
            // only ever the picked entry's code
            if (picked.Totp is { } own && SixDigits.IsMatch(own)) return new OtpFetchResult(own, null);
            if (await Timed(t => client.GetTotpAsync(picked.Uuid, t), TimeSpan.FromSeconds(15)) is { } code &&
                SixDigits.IsMatch(code))
                return new OtpFetchResult(code, null);
            return new OtpFetchResult(null, $"No KeePassXC entry for {url} has a one-time password (TOTP).");
        }
        catch (KeePassXcException e)
        {
            return new OtpFetchResult(null, e.Code switch
            {
                KeePassXcException.NoLogins => $"KeePassXC has no entry for {url}. Put that URL in the entry that holds the authenticator.",
                KeePassXcException.Denied => "KeePassXC refused access to the entry.",
                KeePassXcException.DatabaseLocked => "KeePassXC is locked. Unlock your database, then try again.",
                _ => "KeePassXC didn't give the code: " + e.Message,
            });
        }
        finally { Stop(); }
    }

    // entries with a code beat ones without (a login next to a separate authenticator entry). With several, the one
    // whose login is the SE ID, so never another account's code
    internal static (KeePassXcEntry? Entry, string? Problem) PickKeePassXcEntry(IReadOnlyList<KeePassXcEntry> entries, string seaId, string url)
    {
        if (entries.Any(e => !string.IsNullOrEmpty(e.Totp)))
            entries = entries.Where(e => !string.IsNullOrEmpty(e.Totp)).ToList();
        if (entries.Count == 0) return (null, $"No KeePassXC entry for {url} has a one-time password (TOTP).");
        if (entries.Count == 1) return (entries[0], null);
        // a blank SE ID matches nothing, not even a blank user name
        var mine = string.IsNullOrWhiteSpace(seaId) ? null
                 : entries.FirstOrDefault(e => string.Equals(e.Login, seaId, StringComparison.OrdinalIgnoreCase));
        return mine is null
            ? (null, $"More than one KeePassXC entry for {url} has a one-time password. Set the user name of the right entry to your Square Enix ID.")
            : (mine, null);
    }

    // deletes need no wrap key, so the key file's never touched (and it never throws)
    public static void ForgetKeePassXcIfUnused(IEnumerable<Profile> profiles) =>
        ForgetKeePassXcIfUnused(profiles, static () => new WindowsCredentialStore());

    internal static void ForgetKeePassXcIfUnused(IEnumerable<Profile> profiles, Func<WindowsCredentialStore> keylessStore)
    {
        try
        {
            if (profiles.Any(p => p.OtpSource == KeePassXC)) return;
            keylessStore().Delete(KeePassXcKey);
        }
        catch (Exception ex) { Log.Warning(ex, "Removing the KeePassXC association failed"); }
    }

    // only from KeePassXC's own Program Files folder, and only if that's admin-only. Never from the registry or a
    // browser manifest, anything running as the user can edit those
    private static string? FindKeePassXcProxy()
    {
        // ProgramW6432, as this is a 32-bit app and ProgramFiles is the (x86) one
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            if (root is { Length: > 0 } && Path.Combine(root, "KeePassXC", "keepassxc-proxy.exe") is var p && File.Exists(p) && PathSafety.IsAdminOnly(p))
                return p;
        return null;
    }

    // a tool in a folder non-admin programs can change isn't run as admin, one of them could swap it
    private static string? Unprotected(string tool, string machineInstall) =>
        PathSafety.IsAdminOnly(tool) ? null
        : $"Provenance runs as an administrator, so it won't run {Path.GetFileName(tool)} from {Path.GetDirectoryName(tool)}. Programs without administrator rights can change that folder. Install the tool for all users instead ({machineInstall}).";

    private static string? FindTool(string name, bool allowCmd = false)
    {
        var dirs = ToolDirs(Environment.GetEnvironmentVariable("PATH"),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
                            Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 } pf64 ? Path.Combine(pf64, "WinGet", "Links") : "",
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var ext in allowCmd ? new[] { ".exe", ".cmd" } : new[] { ".exe" })
            foreach (var d in dirs)
            {
                try { if (Path.Combine(d, name + ext) is var f && File.Exists(f)) return f; }
                catch { }
            }
        return null;
    }

    // fully qualified only. A relative entry (".", "bin", "C:x", "\x") would resolve against whatever folder the app
    // happens to run in
    internal static List<string> ToolDirs(string? pathVariable, params string[] extra) =>
        (pathVariable ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Concat(extra)
            .Select(d => d.Trim().Trim('"'))
            .Where(d => d.Length > 0 && Path.IsPathFullyQualified(d))
            .ToList();

    private sealed record RunResult(int Exit, string Out, string Err);

    // no window and stdin closed straight away, so it can never sit waiting for typing
    private static async Task<RunResult> RunAsync(string exe, IEnumerable<string> args, IDictionary<string, string>? env,
                                                  TimeSpan timeout, CancellationToken ct)
    {
        bool cmd = IsCmd(exe);
        // cmd.exe by its full path, a bare name gets looked for in the app folder first
        var psi = new ProcessStartInfo(cmd ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            // its own folder, never whatever folder the elevated app happens to be in
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        psi.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        if (cmd) foreach (var a in new[] { "/d", "/c", exe }) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
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
            try { p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileNameWithoutExtension(exe)} didn't answer within {timeout.TotalSeconds:0} seconds.");
        }
        return new RunResult(p.ExitCode, await o, await e);
    }

    // "[ERROR] 2026/10/19 20:00:00 authorization prompt dismissed" -> "authorization prompt dismissed"
    private static string CleanError(string err, string tool)
    {
        var line = err.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        line = Regex.Replace(line, @"^\[ERROR\]\s+\S+\s+\S+\s+", "");
        return line.Length > 0 ? line : $"{tool} stopped without saying why.";
    }
}
