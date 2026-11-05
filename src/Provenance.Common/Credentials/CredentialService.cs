using System.Runtime.ExceptionServices;

namespace Provenance.Common.Credentials;

// The wrap key's opened on first use, so a broken key file can't stop the app starting. Names, header tests and
// deletes don't need it
public sealed class CredentialService : ICredentialService
{
    private readonly WindowsCredentialStore _store;
    private readonly WrappedStoreCache<WindowsCredentialStore> _wrapped;

    public CredentialService()
        : this(new WindowsCredentialStore(), () => new WindowsCredentialStore(wrapKey: CredentialKey.Open())) { }

    public CredentialService(WindowsCredentialStore store) : this(store, () => store) { }

    internal CredentialService(WindowsCredentialStore store, Func<WindowsCredentialStore> openWrapped)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(openWrapped);
        _store = store;
        _wrapped = new WrappedStoreCache<WindowsCredentialStore>(openWrapped);
    }

    private WindowsCredentialStore Wrapped => _wrapped.Get();

    // so the first load or save on the UI thread never sits waiting on the key file's lock
    public Task OpenKeyInBackground() => Task.Run(() =>
    {
        try { _ = Wrapped; }
        catch (CredentialKeyUnavailableException) { }
    });

    public PolAccount? LoadPolAccount(string profileId) =>
        ReadOrNull(KeyFor(CredentialTargets.Pol, profileId)) is { } c ? new PolAccount(c.UserName, c.Secret) : null;

    public SquareEnixAccount? LoadSquareEnixAccount(string profileId) =>
        ReadOrNull(KeyFor(CredentialTargets.Sea, profileId)) is { } c ? new SquareEnixAccount(c.UserName, c.Secret) : null;

    // an entry the wrap key can't read (a lost key) loads as null, and the user types the details in again
    private StoredCredential? ReadOrNull(string key)
    {
        try { return Wrapped.Read(key); }
        catch (CredentialUnreadableException) { return null; }
    }

    public bool HasUsableDetails(string profileId) =>
        _store.HasWrappedSecret(KeyFor(CredentialTargets.Pol, profileId)) &&
        _store.HasWrappedSecret(KeyFor(CredentialTargets.Sea, profileId));

    public (string? PolId, string? SeaId) ReadIds(string profileId) =>
        (ReadPolId(profileId), _store.ReadUserName(KeyFor(CredentialTargets.Sea, profileId)));

    public string? ReadPolId(string profileId) => _store.ReadUserName(KeyFor(CredentialTargets.Pol, profileId));

    public void SavePolAccount(string profileId, PolAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        Wrapped.Save(KeyFor(CredentialTargets.Pol, profileId), account.PolId, account.Password);
    }

    public void SaveSquareEnixAccount(string profileId, SquareEnixAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        Wrapped.Save(KeyFor(CredentialTargets.Sea, profileId), account.Username, account.Password);
    }

    public void DeletePolAccount(string profileId) => Delete(KeyFor(CredentialTargets.Pol, profileId));

    public void DeleteSquareEnixAccount(string profileId) => Delete(KeyFor(CredentialTargets.Sea, profileId));

    internal Action<string>? BeforeDeleteForTest { get; set; }

    private void Delete(string key)
    {
        BeforeDeleteForTest?.Invoke(key);
        _store.Delete(key);
    }

    // a failed POL delete mustn't leave the SEA entry behind
    public void DeleteProfile(string profileId)
    {
        Exception? first = null;
        try { DeletePolAccount(profileId); }
        catch (Exception ex) { first = ex; }
        try { DeleteSquareEnixAccount(profileId); }
        catch (Exception ex) { first ??= ex; }
        if (first != null) ExceptionDispatchInfo.Throw(first);
    }

    // the store turns "POL:<profile id>" into the target "Provenance:POL:<profile id>"
    private static string KeyFor(string baseKey, string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return $"{baseKey}:{profileId}";
    }
}
