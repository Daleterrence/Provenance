using System.Security.Cryptography;
using System.Text;
using Provenance.Common.Credentials;
using Provenance.Common.Settings;

namespace Provenance.Common.Login;

public sealed class RefusedPasswords(ISettingsService settings, Func<byte[]> key)
{
    public RefusedPasswords(ISettingsService settings) : this(settings, CredentialKey.InUse) { }

    public int Count(string polId) => settings.Current.PasswordRefusals.GetValueOrDefault(NameOf(polId));

    public void Add(string polId) => Change(polId, n => n + 1);

    public void MarkLocked(string polId) => Change(polId, n => Math.Max(n, 3));

    public void Clear(string polId)
    {
        var name = NameOf(polId);
        if (settings.Current.PasswordRefusals.ContainsKey(name)) settings.SaveChange(s => s.PasswordRefusals.Remove(name));
    }

    private void Change(string polId, Func<int, int> to)
    {
        var name = NameOf(polId);
        settings.SaveChange(s => s.PasswordRefusals[name] = to(s.PasswordRefusals.GetValueOrDefault(name)));
    }

    // the ID itself is never kept, and without the credential key the names can't be matched to one
    private string NameOf(string polId)
    {
        var bytes = key();
        try { return Convert.ToHexString(HMACSHA256.HashData(bytes, Encoding.UTF8.GetBytes(polId.Trim().ToUpperInvariant())), 0, 16).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
