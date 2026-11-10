using System.Diagnostics;
using Provenance.Common.Credentials;
using Provenance.Common.Security;
using Provenance.Common.Settings;
using Serilog;

namespace Provenance.Common.Otp;

public static class KeePassXc
{
    private const string AssociationKey = "keepassxc-association";
    private const string NoAnswer = "KeePassXC didn't answer.";

    private static StoredCredential? ReadAssociation(WindowsCredentialStore store)
    {
        try { return store.Read(AssociationKey); }
        catch (CredentialUnreadableException) { return null; }
    }

    // wrapped with the saved accounts' key, and opened on a worker so the lock wait never lands on the UI thread
    internal static Task<WindowsCredentialStore> AssociationStoreAsync() => CredentialKey.WrappedAsync();

    internal static async Task<OtpFetchResult> FetchAsync(string url, string squareEnixId, CancellationToken ct)
    {
        var proxy = FindProxy();
        if (proxy is null)
            return OtpSources.Problem("not installed", "Provenance needs KeePassXC installed with its installer.", OtpFix.TypeInstead);
        var store = await AssociationStoreAsync().WaitAsync(ct);
        using var p = Process.Start(ToolProcess.StartInfo(proxy, [])) ?? throw new InvalidOperationException("keepassxc-proxy did not start.");
        _ = p.StandardError.ReadToEndAsync(ct);
        var calls = new ProxyCalls(p, ct);
        try
        {
            using var client = new KeePassXcClient(p.StandardInput.BaseStream, p.StandardOutput.BaseStream);
            await calls.CallNoResult(t => client.ExchangeKeysAsync(t), TimeSpan.FromSeconds(10),
                                     "KeePassXC isn't open, or its browser integration is off.");
            if (await PairAsync(client, store, calls) is not { } pair)
                return OtpSources.Problem("not paired", "KeePassXC didn't accept the connection.");
            var (useId, useKey) = pair;
            Task<IReadOnlyList<KeePassXcEntry>> Logins(bool unlock, int secs) =>
                calls.Call(t => client.GetLoginsAsync(url, useId, useKey, unlock, t), TimeSpan.FromSeconds(secs));
            IReadOnlyList<KeePassXcEntry> entries;
            try { entries = await Logins(false, 60); }
            catch (KeePassXcException e) when (e.Code == KeePassXcException.DatabaseLocked)
            {
                // KeePassXC shows its unlock dialog, then says when the database is open
                try { await Logins(true, 15); }
                catch (KeePassXcException) { }
                await calls.CallNoResult(t => client.WaitForUnlockAsync(t), TimeSpan.FromMinutes(2), "KeePassXC is still locked.");
                entries = await Logins(false, 60);
            }
            var (picked, pickProblem) = PickEntry(entries, squareEnixId, url);
            if (picked is null) return OtpSources.Problem(entries.Count == 0 ? "no entry with a code" : "several entries", pickProblem!, OtpFix.TypeInstead);
            // only ever the picked entry's code
            if (picked.Totp is { } own && OtpSources.SixDigits.IsMatch(own)) return new OtpFetchResult(own, null);
            if (await calls.Call(t => client.GetTotpAsync(picked.Uuid, t), TimeSpan.FromSeconds(15)) is { } code &&
                OtpSources.SixDigits.IsMatch(code))
                return new OtpFetchResult(code, null);
            return OtpSources.Problem("no code", $"No KeePassXC entry for {url} has a one-time password.", OtpFix.TypeInstead);
        }
        catch (KeePassXcException e)
        {
            return OtpSources.Problem($"KeePassXC error {e.Code}", e.Code switch
            {
                KeePassXcException.NoLogins => $"KeePassXC has no entry for {url}.",
                KeePassXcException.Denied => "KeePassXC refused access to the entry.",
                KeePassXcException.DatabaseLocked => "KeePassXC is locked.",
                _ => "KeePassXC didn't give the code.",
            }, e.Code == KeePassXcException.NoLogins ? OtpFix.TypeInstead : OtpFix.TryAgain);
        }
        finally { calls.Stop(); }
    }

    // the saved connection if KeePassXC still knows it, else a new one (which makes KeePassXC ask to allow the app). Null
    // when even the new one isn't accepted
    private static async Task<(string Id, byte[] Key)?> PairAsync(KeePassXcClient client, WindowsCredentialStore store, ProxyCalls calls)
    {
        Task<bool> Paired(string id, byte[] key) =>
            calls.Call(t => client.TestAssociateAsync(id, key, t), TimeSpan.FromSeconds(15));
        var saved = ReadAssociation(store);
        string? savedId = saved?.UserName;
        byte[]? savedKey = null;
        if (saved is { } c)
        {
            var buf = new byte[64];
            if (Convert.TryFromBase64String(c.Secret, buf, out int n) && n == 32) savedKey = buf[..32];   // else pair again
        }
        if (savedId is not null && savedKey is not null && await Paired(savedId, savedKey)) return (savedId, savedKey);
        var key = KeePassXcClient.NewIdKey();
        var id = await calls.Call(t => client.AssociateAsync(key, t), TimeSpan.FromMinutes(2), "KeePassXC didn't allow Provenance in time.");
        store.Save(AssociationKey, id, Convert.ToBase64String(key));
        return await Paired(id, key) ? (id, key) : null;
    }

    // reads on the proxy's pipe ignore cancellation, so the limit (or a cancel) kills the proxy instead
    private sealed class ProxyCalls(Process proxy, CancellationToken ct)
    {
        public void Stop() { try { if (!proxy.HasExited) proxy.Kill(); } catch { } }

        public async Task<T> Call<T>(Func<CancellationToken, Task<T>> work, TimeSpan limit, string message = NoAnswer)
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

        public Task CallNoResult(Func<CancellationToken, Task> work, TimeSpan limit, string message) =>
            Call(async t => { await work(t); return true; }, limit, message);
    }

    // entries with a code beat ones without (a login next to a separate authenticator entry). With several, the one
    // whose login is the SE ID, so never another account's code
    internal static (KeePassXcEntry? Entry, string? Problem) PickEntry(IReadOnlyList<KeePassXcEntry> entries, string squareEnixId, string url)
    {
        if (entries.Any(e => !string.IsNullOrEmpty(e.Totp)))
            entries = entries.Where(e => !string.IsNullOrEmpty(e.Totp)).ToList();
        if (entries.Count == 0) return (null, $"No KeePassXC entry for {url} has a one-time password.");
        if (entries.Count == 1) return (entries[0], null);
        // a blank SE ID matches nothing, not even a blank user name
        var mine = string.IsNullOrWhiteSpace(squareEnixId) ? null
                 : entries.FirstOrDefault(e => string.Equals(e.Login, squareEnixId, StringComparison.OrdinalIgnoreCase));
        return mine is null
            ? (null, $"More than one KeePassXC entry for {url} has a one-time password. Set the username of the right one to your Square Enix ID.")
            : (mine, null);
    }

    // deletes need no wrap key, so the key file's never touched (and it never throws)
    public static void ForgetIfUnused(IEnumerable<Account> profiles) =>
        ForgetIfUnused(profiles, static () => CredentialKey.Keyless);

    internal static void ForgetIfUnused(IEnumerable<Account> profiles, Func<WindowsCredentialStore> keylessStore)
    {
        try
        {
            if (profiles.Any(p => p.OtpSource == OtpSources.KeePassXC)) return;
            keylessStore().Delete(AssociationKey);
        }
        catch (Exception ex) { Log.Warning(ex, "Removing the KeePassXC association failed"); }
    }

    // only from KeePassXC's own Program Files folder, and only if that's admin-only. Never from the registry or a
    // browser manifest, anything running as the user can edit those
    private static string? FindProxy()
    {
        // ProgramW6432, as this is a 32-bit app and ProgramFiles is the (x86) one
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), AppPaths.ProgramFiles })
            if (root is { Length: > 0 } && Path.Combine(root, "KeePassXC", "keepassxc-proxy.exe") is var p && File.Exists(p) && PathSafety.IsAdminOnly(p))
                return p;
        return null;
    }
}
