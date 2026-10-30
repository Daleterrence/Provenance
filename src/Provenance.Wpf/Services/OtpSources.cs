using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using Provenance.Common.Credentials;
using Provenance.Common.Locks;
using Provenance.Common.Otp;

namespace Provenance.Wpf.Services;

// what the code dialog offers when there's no code
public enum OtpFix { TryAgain, Install, OpenOnePasswordSettings, TypeInstead, MasterPassword }

// one plain line and its button. Outcome only goes in the log, never the item, the tool's output or the code
public sealed record OtpFetchResult(string? Code, string? Problem, OtpFix Fix = OtpFix.TryAgain, string? Tool = null)
{
    internal string Outcome { get; init; } = "";
}

public sealed record OnePasswordAccount(string Id, string Url, string Email);

public enum BitwardenLogin { LoggedIn, AlreadyLoggedIn, NotLoggedIn, ServerRefused, NoAnswer, NoTool }

// The code from a password manager that's unlocked on this PC, read at login, passed on once and never stored
public static class OtpSources
{
    public const string Bitwarden = "bitwarden", OnePassword = "1password", KeePassXC = "keepassxc";
    private const string KeePassXcKey = "keepassxc-association";
    private const string NoAnswer = "KeePassXC didn't answer.";
    private const string OnePasswordSettingsLink = "onepassword://settings/developers";
    private static readonly Regex SixDigits = new(@"^\d{6}$");
    private static readonly Regex AccountId = new(@"^[A-Za-z0-9]{1,64}$");
    private static readonly Dictionary<string, string> OnePasswordEnv = new() { ["OP_BIOMETRIC_UNLOCK_ENABLED"] = "true" };

    public const string OnePasswordWaitText = "Getting the code from 1Password. Confirm with Windows Hello when it asks.";
    public const string OpenSettingsFailedText = "Windows didn't open 1Password.";
    public const string BitwardenLoginText = "Bitwarden asks for your email, master password and two-step login code in a window of its own.";
    public const string DashText = "Provenance can't use an item name that starts with a dash.";

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
                      "Provenance gets the code from the Bitwarden command-line tool. Bitwarden only gives codes to Premium accounts."),
        OnePassword => ("Item name or ID in 1Password",
                        "Provenance gets the code from the 1Password app. In 1Password, turn on Integrate with 1Password CLI (Settings > Developer)."),
        KeePassXC => ("Entry URL in KeePassXC",
                      "Provenance gets the code from KeePassXC, using the URL saved in the entry. Turn on Enable browser integration in KeePassXC (Tools > Settings > Browser Integration)."),
        _ => ("", ""),
    };

    // never throws, a problem comes back as text
    public static async Task<OtpFetchResult> FetchAsync(string source, string item, string account, string seaId, string? bitwardenPassword,
                                                        CancellationToken ct)
    {
        OtpFetchResult r;
        try
        {
            r = source switch
            {
                OnePassword => await FromOnePasswordAsync(item, account, ct),
                Bitwarden => await FromBitwardenAsync(item, bitwardenPassword, ct),
                KeePassXC => await FromKeePassXcAsync(item, seaId, ct),
                _ => Problem("no manager", "This account doesn't have a password manager chosen.", OtpFix.TypeInstead),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { r = Problem("cancelled", "Cancelled."); }
        // KeePassXC's waits say which one ran out, op and bw only ever didn't answer
        catch (TimeoutException ex) { r = Problem("timed out", source == KeePassXC ? ex.Message : $"{Name(source)} didn't answer."); }
        catch (CredentialKeyUnavailableException ex) { r = Problem("key file unavailable", ex.Message, OtpFix.TypeInstead); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading the one-time password from {Source} failed", source);
            r = Problem("failed", $"{Name(source)} didn't give the code.");
        }
        Log.Information("One-time password from {Source}: {Outcome}", Name(source), r.Code is null ? r.Outcome : "got the code");
        return r;
    }

    private static OtpFetchResult Problem(string outcome, string text, OtpFix fix = OtpFix.TryAgain, string? tool = null) =>
        new(null, text, fix, tool) { Outcome = outcome };

    private static OtpFetchResult ToolProblem(string tool, ToolFind found) =>
        found.CantCheck ? Problem("couldn't check", $"Provenance couldn't check the {CliTools.Name(tool)} command-line tool.", OtpFix.Install, tool)
        : found.TooOld ? Problem("too old", $"The {CliTools.Name(tool)} command-line tool is too old for Provenance.", OtpFix.Install, tool)
        : found.RefusedFolder is not null ? Problem("refused", $"Provenance can't use your copy of the {CliTools.Name(tool)} command-line tool.", OtpFix.Install, tool)
        : Problem("not installed", $"The {CliTools.Name(tool)} command-line tool isn't installed.", OtpFix.Install, tool);

    // a leading dash would make the item an option, and settings.json is the user's, so this is checked at the run too
    public static bool StartsWithDash(string item) => item.TrimStart().StartsWith('-');

    private static OtpFetchResult CodeFrom(RunResult r, Func<string, (string Outcome, string Text, OtpFix Fix)> problem)
    {
        var code = r.Out.Trim();
        if (r.Exit == 0 && SixDigits.IsMatch(code)) return new OtpFetchResult(code, null);
        var (outcome, text, fix) = problem(r.Err);
        return Problem($"{outcome} (exit {r.Exit})", text, fix);
    }

    private static async Task<OtpFetchResult> FromOnePasswordAsync(string item, string account, CancellationToken ct)
    {
        if (StartsWithDash(item)) return Problem("item starts with a dash", DashText, OtpFix.TypeInstead);
        using var found = await Task.Run(() => CliTools.Find(CliTools.OnePassword), ct);
        if (found.Path is not { } op) return ToolProblem(CliTools.OnePassword, found);
        try
        {
            List<string> args = ["item", "get", item, "--otp"];
            if (AccountId.IsMatch(account)) args.AddRange(["--account", account]);
            // the app confirms with Windows Hello, hence the long wait
            var r = await RunAsync(op, args, OnePasswordEnv, TimeSpan.FromSeconds(90), ct);
            return CodeFrom(r, err => OnePasswordProblem(err, item));
        }
        finally { CliTools.UpdateInBackground(CliTools.OnePassword); }
    }

    // the ones people actually hit, by op's own wording
    internal static (string Outcome, string Text, OtpFix Fix) OnePasswordProblem(string err, string item)
    {
        bool Says(string s) => err.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Says("No accounts configured for use with 1Password CLI") || Says("account is not signed in"))
            return ("integration off", "1Password's CLI integration is turned off.", OtpFix.OpenOnePasswordSettings);
        if (Says("cannot connect to 1Password app") || Says("LostConnectionToApp"))
            return ("app not reachable", "1Password isn't open.", OtpFix.TryAgain);
        if (Says("authorization prompt dismissed"))
            return ("prompt closed", "The 1Password prompt was closed.", OtpFix.TryAgain);
        if (Says("isn't an item"))
            return ("not an item", $"1Password has no item called \"{item}\".", OtpFix.TypeInstead);
        if (Says("More than one item matches"))
            return ("several items", $"More than one 1Password item is called \"{item}\".", OtpFix.TypeInstead);
        return ("unrecognised error", "1Password didn't give the code.", OtpFix.TryAgain);
    }

    // op gets these from the app without Windows Hello. Null when op couldn't say
    public static async Task<IReadOnlyList<OnePasswordAccount>?> OnePasswordAccountsAsync(string op, CancellationToken ct)
    {
        try
        {
            var r = await RunAsync(op, ["account", "list", "--format", "json"], OnePasswordEnv, TimeSpan.FromSeconds(20), ct);
            if (r.Exit != 0)
            {
                Log.Information("Listing 1Password's accounts: exit {Exit}", r.Exit);
                return null;
            }
            return ParseOnePasswordAccounts(r.Out);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing 1Password's accounts failed");
            return null;
        }
    }

    internal static IReadOnlyList<OnePasswordAccount>? ParseOnePasswordAccounts(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<OnePasswordAccount>();
            foreach (var a in doc.RootElement.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                string Text(string name) => a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var id = Text("account_uuid");
                if (AccountId.IsMatch(id) && list.All(x => x.Id != id)) list.Add(new OnePasswordAccount(id, Text("url"), Text("email")));
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    // 1Password mustn't start as admin, so explorer.exe hands the link to the user's own shell, and there's
    // deliberately no fallback that opens it from here
    public static bool OpenOnePasswordSettings()
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
            psi.ArgumentList.Add(OnePasswordSettingsLink);
            using var p = Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open 1Password's developer settings");
            return false;
        }
    }

    private static async Task<OtpFetchResult> FromBitwardenAsync(string item, string? masterPassword, CancellationToken ct)
    {
        if (StartsWithDash(item)) return Problem("item starts with a dash", DashText, OtpFix.TypeInstead);
        using var found = await Task.Run(() => CliTools.Find(CliTools.Bitwarden), ct);
        if (found.Path is not { } bw) return ToolProblem(CliTools.Bitwarden, found);
        try
        {
            var env = new Dictionary<string, string>();
            if (masterPassword is { Length: > 0 })
            {
                // the password only ever goes in that one process's environment, never on a command line
                var u = await RunAsync(bw, ["unlock", "--passwordenv", "PROVENANCE_BW_PASSWORD", "--raw", "--nointeraction"],
                                       new Dictionary<string, string> { ["PROVENANCE_BW_PASSWORD"] = masterPassword }, TimeSpan.FromSeconds(60), ct);
                if (u.Exit != 0 || u.Out.Trim().Length == 0)
                    return Problem($"didn't unlock (exit {u.Exit})", "Bitwarden didn't unlock.", OtpFix.MasterPassword);
                _bitwardenSession = u.Out.Trim();
            }
            if (_bitwardenSession is { } session) env["BW_SESSION"] = session;
            var status = await BitwardenStatusAsync(bw, env, ct);
            if (status == "unauthenticated")
                return Problem("not logged in", "Bitwarden needs logging in from Edit account.", OtpFix.TypeInstead);
            if (status == "locked")
            {
                _bitwardenSession = null;
                return Problem("locked", "Bitwarden is locked.", OtpFix.MasterPassword);
            }
            if (status != "unlocked") return Problem("no status", "Bitwarden didn't answer.");
            var r = await RunAsync(bw, ["get", "totp", item, "--nointeraction"], env, TimeSpan.FromSeconds(30), ct);
            // bw never syncs by itself, so an item added since it logged in isn't there yet
            if (r.Exit != 0 && CleanError(r.Err) == "Not found.")
            {
                var sync = await RunAsync(bw, ["sync", "--nointeraction"], env, TimeSpan.FromSeconds(60), ct);
                Log.Information("Bitwarden item not found, synced (exit {Exit}) and asked again", sync.Exit);
                r = await RunAsync(bw, ["get", "totp", item, "--nointeraction"], env, TimeSpan.FromSeconds(30), ct);
            }
            return CodeFrom(r, err => BitwardenProblem(err, item));
        }
        finally { CliTools.UpdateInBackground(CliTools.Bitwarden); }
    }

    internal static (string Outcome, string Text, OtpFix Fix) BitwardenProblem(string err, string item)
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

    private static async Task<string> BitwardenStatusAsync(string bw, IDictionary<string, string>? env, CancellationToken ct)
    {
        var s = await RunAsync(bw, ["status", "--nointeraction"], env, TimeSpan.FromSeconds(30), ct);
        try { return JsonDocument.Parse(s.Out).RootElement.GetProperty("status").GetString() ?? ""; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return ""; }
    }

    // "bitwarden.com" puts bw back on its own default. Null for a self-hosted address that isn't a plain https URL
    internal static string? BitwardenServer(string picked, string typed)
    {
        if (picked.Length > 0) return picked;
        if (!Uri.TryCreate(typed.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.Host.Length == 0
            || u.UserInfo.Length > 0 || u.Query.Length > 0 || u.Fragment.Length > 0)
            return null;
        return u.AbsoluteUri.TrimEnd('/');
    }

    private static readonly object LoginGate = new();
    private static Task<BitwardenLogin>? _login;   // under LoginGate
    private static Process? _loginWindow;          // under LoginGate

    // the login that's still going, if there is one. Edit account watches it rather than starting another
    public static Task<BitwardenLogin>? BitwardenLoginRunning
    {
        get { lock (LoginGate) return _login is { IsCompleted: false } running ? running : null; }
    }

    internal static void SetLoginForTest(Task<BitwardenLogin>? login)
    {
        lock (LoginGate) _login = login;
    }

    // one window at a time. Closing Edit account only stops watching it, the window's the user's to finish
    public static Task<BitwardenLogin> LogInToBitwardenAsync(string server)
    {
        lock (LoginGate)
        {
            if (_login is { IsCompleted: false } running) return running;
            return _login = Task.Run(() => LogInAsync(server));
        }
    }

    // bw asks for everything in its own window. --quiet so the session key never shows, and it's locked when the
    // window closes
    private static async Task<BitwardenLogin> LogInAsync(string server)
    {
        using var found = await Task.Run(() => CliTools.Find(CliTools.Bitwarden));
        if (found.Path is not { } bw) return BitwardenLogin.NoTool;
        var before = await BitwardenStatusAsync(bw, null, CancellationToken.None);
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
        var config = await RunAsync(bw, ["config", "server", server, "--nointeraction"], null, TimeSpan.FromSeconds(30), CancellationToken.None);
        if (config.Exit != 0)
        {
            Log.Information("Bitwarden login: the server wasn't taken (exit {Exit})", config.Exit);
            return BitwardenLogin.ServerRefused;
        }
        var psi = new ProcessStartInfo(bw) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(bw) ?? "" };
        ToolEnvironment(psi.Environment);
        psi.ArgumentList.Add("login");
        psi.ArgumentList.Add("--quiet");
        try
        {
            using var window = Process.Start(psi) ?? throw new InvalidOperationException("bw did not start.");
            lock (LoginGate) _loginWindow = window;
            await window.WaitForExitAsync();
        }
        finally
        {
            lock (LoginGate) _loginWindow = null;
            await LockAsync(bw, null);
        }
        var after = await BitwardenStatusAsync(bw, null, CancellationToken.None);
        var result = after is "locked" or "unlocked" ? BitwardenLogin.LoggedIn
                   : after == "unauthenticated" ? BitwardenLogin.NotLoggedIn
                   : BitwardenLogin.NoAnswer;
        Log.Information("Bitwarden login: {Result}", result);
        return result;
    }

    private static async Task LockAsync(string bw, string? session)
    {
        try
        {
            var env = session is null ? null : new Dictionary<string, string> { ["BW_SESSION"] = session };
            var r = await RunAsync(bw, ["lock", "--nointeraction"], env, TimeSpan.FromSeconds(30), CancellationToken.None);
            Log.Information("Bitwarden locked (exit {Exit})", r.Exit);
        }
        catch (Exception ex) { Log.Warning(ex, "Locking Bitwarden failed"); }
    }

    // at exit a login window still open is closed (so none finishes after Provenance), then it's locked and the
    // session's forgotten either way. Never throws
    public static async Task LockBitwardenAsync()
    {
        Process? window;
        lock (LoginGate) window = _loginWindow;
        try { window?.Kill(entireProcessTree: true); }
        catch (Exception ex) { Log.Debug(ex, "Closing Bitwarden's login window failed"); }
        var session = Interlocked.Exchange(ref _bitwardenSession, null);
        if (session is null && window is null) return;
        try
        {
            using var found = CliTools.Find(CliTools.Bitwarden);
            if (found.Path is not { } bw) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var env = session is null ? null : new Dictionary<string, string> { ["BW_SESSION"] = session };
            await RunAsync(bw, ["lock", "--nointeraction"], env, TimeSpan.FromSeconds(5), cts.Token);
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
            return Problem("not installed", "Provenance needs KeePassXC installed with its installer.", OtpFix.TypeInstead);
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
        ToolEnvironment(psi.Environment);
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
                         "KeePassXC isn't open, or its browser integration is off.");
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
                                         "KeePassXC didn't allow Provenance in time.");
                        store.Save(KeePassXcKey, id, Convert.ToBase64String(idKey));
                    }
                }
                finally { pairing?.Dispose(); }
                if (!await Paired(id, idKey))
                    return Problem("not paired", "KeePassXC didn't accept the connection.");
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
            if (picked is null) return Problem(entries.Count == 0 ? "no entry with a code" : "several entries", pickProblem!, OtpFix.TypeInstead);
            // only ever the picked entry's code
            if (picked.Totp is { } own && SixDigits.IsMatch(own)) return new OtpFetchResult(own, null);
            if (await Timed(t => client.GetTotpAsync(picked.Uuid, t), TimeSpan.FromSeconds(15)) is { } code &&
                SixDigits.IsMatch(code))
                return new OtpFetchResult(code, null);
            return Problem("no code", $"No KeePassXC entry for {url} has a one-time password.", OtpFix.TypeInstead);
        }
        catch (KeePassXcException e)
        {
            return Problem($"KeePassXC error {e.Code}", e.Code switch
            {
                KeePassXcException.NoLogins => $"KeePassXC has no entry for {url}.",
                KeePassXcException.Denied => "KeePassXC refused access to the entry.",
                KeePassXcException.DatabaseLocked => "KeePassXC is locked.",
                _ => "KeePassXC didn't give the code.",
            }, e.Code == KeePassXcException.NoLogins ? OtpFix.TypeInstead : OtpFix.TryAgain);
        }
        finally { Stop(); }
    }

    // entries with a code beat ones without (a login next to a separate authenticator entry). With several, the one
    // whose login is the SE ID, so never another account's code
    internal static (KeePassXcEntry? Entry, string? Problem) PickKeePassXcEntry(IReadOnlyList<KeePassXcEntry> entries, string seaId, string url)
    {
        if (entries.Any(e => !string.IsNullOrEmpty(e.Totp)))
            entries = entries.Where(e => !string.IsNullOrEmpty(e.Totp)).ToList();
        if (entries.Count == 0) return (null, $"No KeePassXC entry for {url} has a one-time password.");
        if (entries.Count == 1) return (entries[0], null);
        // a blank SE ID matches nothing, not even a blank user name
        var mine = string.IsNullOrWhiteSpace(seaId) ? null
                 : entries.FirstOrDefault(e => string.Equals(e.Login, seaId, StringComparison.OrdinalIgnoreCase));
        return mine is null
            ? (null, $"More than one KeePassXC entry for {url} has a one-time password.")
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

    internal sealed record RunResult(int Exit, string Out, string Err);

    // no window and stdin closed straight away, so it can never sit waiting for typing
    internal static async Task<RunResult> RunAsync(string exe, IEnumerable<string> args, IDictionary<string, string>? env,
                                                  TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            // its own folder, never whatever folder the elevated app happens to be in
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        ToolEnvironment(psi.Environment);
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

    // built from nothing: Windows' own folders on PATH, the profile in Provenance's locked home, nothing else the user's set
    internal static void ToolEnvironment(IDictionary<string, string?> env)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.SystemDirectory;
        var profile = CliTools.Home;
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
        env["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        env["TEMP"] = env["TMP"] = Path.Combine(profile, "Temp");
        env["USERNAME"] = Environment.UserName;
        env["USERDOMAIN"] = Environment.UserDomainName;
        env["COMPUTERNAME"] = Environment.MachineName;
        env["NoDefaultCurrentDirectoryInExePath"] = "1";
    }

    // "[ERROR] 2026/10/19 20:00:00 authorization prompt dismissed" -> "authorization prompt dismissed". bw's colours go too
    private static string CleanError(string err)
    {
        var line = Regex.Replace(err, @"\x1b\[[0-9;]*m", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        line = Regex.Replace(line, @"^\[ERROR\]\s+\S+\s+\S+\s+", "");
        return line;
    }
}
