using System.ComponentModel;
using System.Runtime.ExceptionServices;
using Serilog;

namespace Provenance.Common.Credentials;

public sealed class CredentialService : ICredentialService
{
    private readonly WindowsCredentialStore _store;
    private readonly Func<WindowsCredentialStore> _wrapped;

    public CredentialService() : this(CredentialKey.Keyless, CredentialKey.Wrapped) { }

    public CredentialService(WindowsCredentialStore store) : this(store, () => store) { }

    internal CredentialService(WindowsCredentialStore keyless, Func<WindowsCredentialStore> wrapped)
    {
        ArgumentNullException.ThrowIfNull(keyless);
        ArgumentNullException.ThrowIfNull(wrapped);
        _store = keyless;
        _wrapped = wrapped;
    }

    private WindowsCredentialStore Wrapped => _wrapped();

    public PolAccount? LoadPolAccount(string profileId) =>
        ReadOrNull(KeyFor(CredentialTargets.Pol, profileId)) is { } c ? new PolAccount(c.UserName, c.Secret) : null;

    public SquareEnixAccount? LoadSquareEnixAccount(string profileId) =>
        ReadOrNull(KeyFor(CredentialTargets.Sea, profileId)) is { } c ? new SquareEnixAccount(c.UserName, c.Secret) : null;

    private StoredCredential? ReadOrNull(string key)
    {
        try { return Wrapped.Read(key); }
        catch (CredentialUnreadableException) { return null; }
    }

    private bool _unreadableLogged;

    public bool HasUsableDetails(string profileId)
    {
        try
        {
            return _store.HasWrappedSecret(KeyFor(CredentialTargets.Pol, profileId)) &&
                   _store.HasWrappedSecret(KeyFor(CredentialTargets.Sea, profileId));
        }
        catch (Win32Exception ex)
        {
            if (!_unreadableLogged) Log.Warning(ex, "Could not read the saved account details");
            _unreadableLogged = true;
            return false;
        }
    }

    public bool HasEntries(string profileId) =>
        _store.Exists(KeyFor(CredentialTargets.Pol, profileId)) && _store.Exists(KeyFor(CredentialTargets.Sea, profileId));

    public (string? PolId, string? SeaId) ReadIds(string profileId) =>
        (ReadPolId(profileId), ReadId(KeyFor(CredentialTargets.Sea, profileId)));

    public string? ReadPolId(string profileId) => ReadId(KeyFor(CredentialTargets.Pol, profileId));

    private string? ReadId(string key) => _store.Exists(key) ? ReadOrNull(key)?.UserName : null;

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

    public void DeleteProfile(string profileId)
    {
        Exception? first = null;
        try { DeletePolAccount(profileId); }
        catch (Exception ex) { first = ex; }
        try { DeleteSquareEnixAccount(profileId); }
        catch (Exception ex) { first ??= ex; }
        if (first != null) ExceptionDispatchInfo.Throw(first);
    }

    private static string KeyFor(string baseKey, string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return $"{baseKey}:{profileId}";
    }
}
