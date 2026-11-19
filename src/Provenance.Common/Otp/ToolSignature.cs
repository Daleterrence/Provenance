using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using Provenance.Common.Win32;
using Serilog;

namespace Provenance.Common.Otp;

internal enum Verdict { Genuine, NotGenuine, CantCheck, TooOld }

internal static class ToolSignature
{
    // 1Password's own ID on its signing certificate, the issuing CA changes and this doesn't
    private const string OnePasswordEku = "1.3.6.1.4.1.311.97.661420558.769123285.207353056.500447802";
    // Bitwarden's company number in Delaware, from its EV certificate
    private const string BitwardenCompanyNumber = "7654941";
    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3", TimeStampingEku = "1.3.6.1.5.5.7.3.8";

    internal static SafeFileHandle? Open(string path)
    {
        try { return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static Verdict Check(string path, string tool, bool online)
    {
        using var file = Open(path);
        return file is null ? Verdict.CantCheck : Check(file, path, tool, online);
    }

    internal static Verdict Check(SafeFileHandle file, string path, string tool, bool online)
    {
        using var sig = WinVerify(file, path);
        try
        {
            if (sig.Result != 0)
            {
                Log.Information("WinVerifyTrust said 0x{Result:X8} for {File}", sig.Result, Path.GetFileName(path));
                return (uint)sig.Result is TrustBadDigest or TrustNoSignature or TrustSubjectFormUnknown or TrustExplicitDistrust or CertRevoked
                    ? Verdict.NotGenuine : Verdict.CantCheck;
            }
            if (sig.Chain.Count == 0) return Verdict.CantCheck;
            if (!SignerMatches(sig.Chain[0], tool)) return Verdict.NotGenuine;
            return Pinned(sig, tool, online) switch
            {
                PinResult.Pinned => Verdict.Genuine,
                PinResult.Revoked => Verdict.NotGenuine,
                _ => Verdict.CantCheck,
            };
        }
        catch (CryptographicException ex)
        {
            Log.Warning(ex, "Checking the signature of {File} failed", Path.GetFileName(path));
            return Verdict.CantCheck;
        }
    }

    internal static bool SignerMatches(X509Certificate2 signer, string tool)
    {
        var parts = SubjectParts(signer);
        if (parts is null) return false;
        return tool switch
        {
            ToolFacts.OnePassword => Only(parts, "2.5.4.3", "Agilebits") && Only(parts, "2.5.4.10", "Agilebits") && HasEku(signer, OnePasswordEku),
            ToolFacts.Bitwarden => Only(parts, "2.5.4.3", "Bitwarden Inc.") && Only(parts, "2.5.4.10", "Bitwarden Inc.")
                         && Only(parts, "2.5.4.5", BitwardenCompanyNumber),
            _ => false,
        };
    }

    private static Dictionary<string, List<string>>? SubjectParts(X509Certificate2 cert)
    {
        var parts = new Dictionary<string, List<string>>();
        foreach (var rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.HasMultipleElements) return null;
            var oid = rdn.GetSingleElementType().Value ?? "";
            if (!parts.TryGetValue(oid, out var values)) parts[oid] = values = [];
            values.Add(rdn.GetSingleElementValue() ?? "");
        }
        return parts;
    }

    private static bool Only(Dictionary<string, List<string>> parts, string oid, string value) =>
        parts.TryGetValue(oid, out var values) && values.All(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));

    private static bool HasEku(X509Certificate2 cert, string eku) =>
        cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(u => u.Value == eku));

    internal enum PinResult { Pinned, NotPinned, Revoked }

    private static readonly Lazy<X509Certificate2> MicrosoftRoot = new(() => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(MicrosoftIdentityRoot2020)));
    private static readonly Lazy<X509Certificate2> DigiCertRoot = new(() => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(DigiCertTrustedRootG4)));

    internal static (X509Certificate2 Root, string Ca) PinFor(string tool) => tool == ToolFacts.OnePassword
        ? (MicrosoftRoot.Value, "Microsoft ID Verified Code Signing PCA 2021")
        : (DigiCertRoot.Value, "DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1");

    internal static PinResult Pinned(Signature sig, string tool, bool online)
    {
        var (root, ca) = PinFor(tool);
        return Pinned(sig.Chain[0], sig.Chain, root, ca, online, sig.StampedAt, sig.StampChain);
    }

    internal static PinResult Pinned(X509Certificate2 signer, X509Certificate2Collection extra, X509Certificate2 root, string ca, bool online,
                                     DateTime? stampedAt = null, X509Certificate2Collection? stampChain = null)
    {
        var at = stampedAt is { } stamp && stampChain is { Count: > 0 } && Build(stampChain[0], stampChain, root, TimeStampingEku, stamp, online).Built
            ? stamp : DateTime.UtcNow;
        var (built, links) = Build(signer, extra, root, CodeSigningEku, at, online);
        if (links.Any(l => l.Revoked)) return PinResult.Revoked;
        if (!built) return PinResult.NotPinned;
        for (int i = 1; i < links.Count - 1; i++)
            if (links[i].Name == ca) return PinResult.Pinned;
        return PinResult.NotPinned;
    }

    private sealed record Link(string Name, bool Revoked);

    private static (bool Built, List<Link> Links) Build(X509Certificate2 leaf, X509Certificate2Collection extra, X509Certificate2 root, string eku,
                                                       DateTime at, bool online)
    {
        using var chain = new X509Chain();
        var policy = chain.ChainPolicy;
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        policy.CustomTrustStore.Add(root);
        policy.ExtraStore.AddRange(extra);
        policy.DisableCertificateDownloads = true;
        policy.ApplicationPolicy.Add(new Oid(eku));
        policy.VerificationTime = at.ToLocalTime();
        policy.VerificationTimeIgnored = false;
        policy.RevocationMode = online ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
        policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        policy.VerificationFlags = X509VerificationFlags.IgnoreEndRevocationUnknown | X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown;
        bool built = chain.Build(leaf);
        var elements = chain.ChainElements;
        var links = elements.Select(e => new Link(e.Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                                                  e.ChainElementStatus.Any(s => s.Status.HasFlag(X509ChainStatusFlags.Revoked)))).ToList();
        bool endsAtPin = elements.Count >= 3 && elements[elements.Count - 1].Certificate.RawData.AsSpan().SequenceEqual(root.RawData);
        return (built && endsAtPin, links);
    }

    internal sealed record Signature(int Result, X509Certificate2Collection Chain, DateTime? StampedAt, X509Certificate2Collection StampChain) : IDisposable
    {
        public void Dispose()
        {
            foreach (var c in Chain) c.Dispose();
            foreach (var c in StampChain) c.Dispose();
        }
    }

    private static Signature WinVerify(SafeFileHandle file, string path)
    {
        var chain = new X509Certificate2Collection();
        var stampChain = new X509Certificate2Collection();
        DateTime? stampedAt = null;
        bool added = false;
        file.DangerousAddRef(ref added);
        var info = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path, hFile = file.DangerousGetHandle() };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(info, pFile, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,              // WTD_UI_NONE
                fdwRevocationChecks = 0,     // WTD_REVOKE_NONE
                dwUnionChoice = 1,           // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 1,           // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x10,          // WTD_REVOCATION_CHECK_NONE
            };
            var action = GenericVerifyV2;
            int result = Wintrust.WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            try
            {
                var prov = Wintrust.WTHelperProvDataFromStateData(data.hWVTStateData);
                var signer = prov == IntPtr.Zero ? IntPtr.Zero : Wintrust.WTHelperGetProvSignerFromChain(prov, 0, false, 0);
                ReadChain(signer, chain);
                var stamp = signer == IntPtr.Zero ? IntPtr.Zero : Wintrust.WTHelperGetProvSignerFromChain(prov, 0, true, 0);
                // CRYPT_PROVIDER_SGNR: a DWORD, the FILETIME, then dwError after the chain pointer, dwSignerType and psSigner
                int errorAt = (16 + IntPtr.Size + 4 + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size + IntPtr.Size;
                if (stamp != IntPtr.Zero && Marshal.ReadInt32(stamp, errorAt) == 0)
                {
                    stampedAt = DateTime.FromFileTimeUtc(Marshal.ReadInt64(stamp, 4));
                    ReadChain(stamp, stampChain);
                }
                return new Signature(result, chain, stampedAt, stampChain);
            }
            finally
            {
                data.dwStateAction = 2;      // WTD_STATEACTION_CLOSE
                Wintrust.WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
            if (added) file.DangerousRelease();
        }
    }

    // csCertChain sits at 12, after a DWORD and a FILETIME
    private static void ReadChain(IntPtr sgnr, X509Certificate2Collection into)
    {
        int count = sgnr == IntPtr.Zero ? 0 : Math.Min(Marshal.ReadInt32(sgnr, 12), 16);
        for (uint i = 0; i < count; i++)
        {
            var cert = Wintrust.WTHelperGetProvCertFromChain(sgnr, i);
            var context = cert == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(cert, IntPtr.Size);
            if (context == IntPtr.Zero) break;
            into.Add(new X509Certificate2(context));
        }
    }

    internal static Signature? VerifiedSignature(string path)
    {
        using var file = Open(path);
        return file is null ? null : WinVerify(file, path);
    }

    private const string MicrosoftIdentityRoot2020 =
        "MIIFzDCCA7SgAwIBAgIQVJjS0dRbGZVIE3nIEcCHmTANBgkqhkiG9w0BAQwFADB3MQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9z" +
        "b2Z0IENvcnBvcmF0aW9uMUgwRgYDVQQDEz9NaWNyb3NvZnQgSWRlbnRpdHkgVmVyaWZpY2F0aW9uIFJvb3QgQ2VydGlmaWNhdGUgQXV0" +
        "aG9yaXR5IDIwMjAwHhcNMjAwNDE2MTgzNjE2WhcNNDUwNDE2MTg0NDQwWjB3MQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0" +
        "IENvcnBvcmF0aW9uMUgwRgYDVQQDEz9NaWNyb3NvZnQgSWRlbnRpdHkgVmVyaWZpY2F0aW9uIFJvb3QgQ2VydGlmaWNhdGUgQXV0aG9y" +
        "aXR5IDIwMjAwggIiMA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQCzkSoHgwZn/Z6d4MfAt6TmQgR/D6bbX/vVWtdFoPt3C/CA86Zt" +
        "Wk15U9ighoRXRSDHolT7x6K/isduNfOiFcQvTuNKhZZJDf++mdgU9rwnB+5Cmyv1C5IG5P1pE2WokXLymITrgz0O5NdxEkghyw3t9kdJ" +
        "t5v5yccXtoRP/7ismtdzZ0mF44a9N0DQJYbU3rXCbWJq1al4vC1vSfnlbBQU/RTH02UWN97LbrxeKY39YpsVLNYF5rmJMjOjYsfX1lJn" +
        "CMQu9FYrnguHzOyntKaq6wXNGVelOgsEJxyRZ54tYi0vHr7awCDLBBnKM/uJvpjicqByNb554ZyDb+RtF2+Q8z0AhnU4jtDgSZq729P4" +
        "MMrVV4hoTXLTv21/cdj9vQ2ukmRIt1tveSa1zZuVIYTR7w8yPXtXjPNFB0x84F4YDjV2i22eyzZ0qwX44HNdMlaUZ5clCsY1PZSX58FE" +
        "i4D9wfj0dBnlMPYG+yFXPgYci2sVhidJe4KTylnodUfoPzj0x1N5oLa04lxR771fOMET5ngMlVouxUBZKMwPJMDsugl3I5k4prYc2se6" +
        "ILbXN9h/N68I4ztx225zG32ZcrDkhjNZdLUWAHtQbcaGE9r9xDmCPSQAmmDaupTABVEsNKxQmROHu7MFgLJNMAJcuCaDXbRjc++uI5VP" +
        "YCi+N9VbpQIDAQABo1QwUjAOBgNVHQ8BAf8EBAMCAYYwDwYDVR0TAQH/BAUwAwEB/zAdBgNVHQ4EFgQUyH7SaoUqG8oZmAQHJ89QEE9o" +
        "qKIwEAYJKwYBBAGCNxUBBAMCAQAwDQYJKoZIhvcNAQEMBQADggIBAK9q3eYZ5y2UQxlOy+lQlWSlA5ECi+I2gDsVolLCFhm2alpddEMw" +
        "9Jv/YHQJsSEekBZtxSSPXGaIY/RPzH3yEkxAEIsBn9qpyK7ylRvPnQXrST50oGhb5VYsZRyCflPaVtlGF3mSRcQQNghSKRfLL6byftRp" +
        "JIoej7BzDcwcSquyqu2nkWMBZCKoMrh+MiizZ3MtkbTcMQEL90cKpvHXSu1WYMQsCKN7QLC8dCdSh9a+iN03ioluZ4gd9cldoP62qzqA" +
        "1xqXPBc2IkEerE3Vg+Y8OL1PMOlUqdO2BMMydmG7sBjFKxizwIDVt5WwXlFNIvzsWKro2JS0pS7tkt7nGHwhV91VY/e/bc0f0qZ3KHDH" +
        "4ls6WwjSW07IAJaz4YM2r4YKZVx09ursemp0oPBL7u+Uo6xQ8oft1zowg8n7fVe+5eP4QcrlZK6zo+xY7IWazO+56vNWGLlcc5qvxXcX" +
        "g1nbNxoYclSlQdK2I3WjQ5rld3yWebdBjb/s3ICgn9F3dVhfNRPgJRpnC33OJfoHCuRhIdjUHOUHxjaZ9JbQxhX+Ts3Xroud2xb9BMaS" +
        "vdSI5qmjqrv3ZDg7X8wM0DW+dBkDpsWqTKJhNoI+HfMrvJdd20t4Oy31O+9gI+j17AsjNpWvmGa/U9N7uGlKKpZmacSUxvRfbqyYeIiA" +
        "Blyisu2i";

    private const string DigiCertTrustedRootG4 =
        "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" +
        "6hN35N6MgSGtBxBHEa2HPQfRdbzP82Z+";

    private const uint TrustBadDigest = 0x80096010, TrustNoSignature = 0x800B0100, TrustSubjectFormUnknown = 0x800B0003,
                       TrustExplicitDistrust = 0x800B0111, CertRevoked = 0x800B010C;

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
}
