using System.Runtime.ExceptionServices;
using Provenance.Common.Credentials;

namespace Provenance.Wpf.Services;

// The wrap key's opened on first use, so a broken key file can't stop the app starting. Names, header tests and
// deletes don't need it
public sealed class CredentialService : ICredentialService
{
    private readonly WindowsCredentialStore _store;
    private readonly WrappedStoreCache<WindowsCredentialStore> _wrapped;
    private readonly Func<bool>? _checkKey, _checkKeyBeforeSave;

    public CredentialService()
        : this(new WindowsCredentialStore(), () => new WindowsCredentialStore(wrapKey: CredentialKey.Open()), CredentialKey.CheckFile,
               CredentialKey.CheckFileNow) { }

    public CredentialService(WindowsCredentialStore store) : this(store, () => store) { }

    internal CredentialService(WindowsCredentialStore store, Func<WindowsCredentialStore> openWrapped, Func<bool>? checkKey = null,
                               Func<bool>? checkKeyBeforeSave = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(openWrapped);
        _store = store;
        _wrapped = new WrappedStoreCache<WindowsCredentialStore>(openWrapped);
        _checkKey = checkKey;
        _checkKeyBeforeSave = checkKeyBeforeSave;
    }

    private WindowsCredentialStore Wrapped => _wrapped.Get();

    // so the first load or save on the UI thread never sits waiting on the key file's lock
    public Task OpenKeyInBackground() => Task.Run(() =>
    {
        try { _ = Wrapped; }
        catch (CredentialKeyUnavailableException) { }
    });

    public Task<bool> CheckKeyAsync()
    {
        if (_checkKey is not { } check) return Task.FromResult(false);
        return Task.Run(() =>
        {
            try { return check(); }
            catch (CredentialKeyUnavailableException) { return false; }
        });
    }

    public PolAccount? LoadPolAccount(string profileId) =>
        ReadOrNull(KeyFor(CredentialKeys.Pol, profileId)) is { } c ? new PolAccount(c.UserName, c.Secret) : null;

    public SeaAccount? LoadSeaAccount(string profileId) =>
        ReadOrNull(KeyFor(CredentialKeys.Sea, profileId)) is { } c ? new SeaAccount(c.UserName, c.Secret) : null;

    // an entry the wrap key can't read (a lost key) loads as null, and the user types the details in again
    private StoredCredential? ReadOrNull(string key)
    {
        try { return Wrapped.Read(key); }
        catch (CredentialUnreadableException) { return null; }
    }

    public bool HasUsableDetails(string profileId) =>
        _store.HasWrappedSecret(KeyFor(CredentialKeys.Pol, profileId)) &&
        _store.HasWrappedSecret(KeyFor(CredentialKeys.Sea, profileId));

    public (string? PolId, string? SeaId) ReadIds(string profileId) =>
        (ReadPolId(profileId), _store.ReadUserName(KeyFor(CredentialKeys.Sea, profileId)));

    public string? ReadPolId(string profileId) => _store.ReadUserName(KeyFor(CredentialKeys.Pol, profileId));

    public void SavePolAccount(string profileId, PolAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        ForSave().Save(KeyFor(CredentialKeys.Pol, profileId), account.PolId, account.Password);
    }

    public void SaveSeaAccount(string profileId, SeaAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        ForSave().Save(KeyFor(CredentialKeys.Sea, profileId), account.Username, account.Password);
    }

    // rereads the key file first, so a key replaced while the account dialog was open gets caught. The UI thread
    // only waits on that read, never on a lock
    private WindowsCredentialStore ForSave()
    {
        if (_checkKeyBeforeSave is { } now)
            Task.Run(() =>
            {
                try { now(); }
                catch (CredentialKeyUnavailableException) { }
            }).GetAwaiter().GetResult();
        return Wrapped;
    }

    public void DeletePolAccount(string profileId) => Delete(KeyFor(CredentialKeys.Pol, profileId));

    public void DeleteSeaAccount(string profileId) => Delete(KeyFor(CredentialKeys.Sea, profileId));

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
        try { DeleteSeaAccount(profileId); }
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
