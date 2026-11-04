using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Provenance.Common.Locks;
using Serilog;

namespace Provenance.Wpf.Services;

public enum ToolInstall { Installed, DownloadFailed, CheckFailed, CantCheck, TooOld, NotSupported }

internal enum Verdict { Genuine, NotGenuine, CantCheck, TooOld }

// Path is held open (no writes, no deletes) until this is disposed, so what runs is the file that was checked
public sealed class ToolFind(string? path, string? refusedFolder, bool cantCheck = false, SafeFileHandle? handle = null, bool tooOld = false) : IDisposable
{
    public string? Path { get; } = path;
    public string? RefusedFolder { get; } = refusedFolder;
    public bool CantCheck { get; } = cantCheck;
    public bool TooOld { get; } = tooOld;

    public void Dispose() => handle?.Dispose();
}

// 1Password's op and Bitwarden's bw run as admin, so each one's signature, signer and CA get checked before every use
public static class CliTools
{
    public const string OnePassword = "op", Bitwarden = "bw";

    // 1Password's own ID on its signing certificate (1password.dev/cli/verify). The issuing CA changes, this doesn't
    private const string OnePasswordEku = "1.3.6.1.4.1.311.97.661420558.769123285.207353056.500447802";
    // Bitwarden's company number in Delaware, from its EV certificate. It stays the same when the cert's renewed
    private const string BitwardenCompanyNumber = "7654941";
    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3", TimeStampingEku = "1.3.6.1.5.5.7.3.8";
    private const string OnePasswordVersionUrl = "https://app-updates.agilebits.com/check/1/0/CLI2/en/2.0.0/N";
    private const string BitwardenDownloadUrl = "https://vault.bitwarden.com/download/?app=cli&platform=windows";
    private static readonly Regex VersionText = new(@"^[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}$");
    private static readonly Regex BitwardenZip =
        new(@"^https://github\.com/bitwarden/clients/releases/download/cli-v([0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6})/bw-windows-\1\.zip$");
    private static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);

    // the versions this was built and tested against. An older one, even a genuine one, never goes in or gets used
    internal static readonly Dictionary<string, Version> Oldest = new() { [OnePassword] = new(2, 41, 1), [Bitwarden] = new(2026, 10, 1) };

    internal static long MaxZip = 200L << 20, MaxExe = 500L << 20;
    internal static TimeSpan StallLimit = TimeSpan.FromSeconds(30);

    private static string? _dirForTest;
    private static HttpMessageHandler? _handlerForTest;
    private static HttpClient? _http, _httpNoRedirect;
    internal static Func<string, ToolFind>? FindForTest;
    internal static string? HomeRootForTest;

    internal static void SetDirForTest(string? dir) => _dirForTest = dir;

    internal static void SetHttpForTest(HttpMessageHandler? handler)
    {
        _handlerForTest = handler;
        _http = _httpNoRedirect = null;
    }

    // C:\ProgramData\Provenance\tools, only Administrators and SYSTEM can change it. Users may read, the 1Password app
    // checks op.exe's signature from the user's side
    internal static string Dir => _dirForTest ?? SecureFiles.RootSubDir("tools", usersMayRead: true);

    // the tools' own profile, per Windows user and admin-only, so nothing they write or unpack to load is the user's
    internal static string Home
    {
        get
        {
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            var home = Path.Combine(HomeRootForTest ?? SecureFiles.RootSubDir("home"), me.User?.Value ?? "user");
            foreach (var sub in new[] { "", @"AppData\Roaming", @"AppData\Local", "Temp" }) Directory.CreateDirectory(Path.Combine(home, sub));
            return home;
        }
    }

    public static string Name(string tool) => tool == OnePassword ? "1Password" : "Bitwarden";

    public static string? ForSource(string source) => source switch
    {
        OtpSources.OnePassword => OnePassword,
        OtpSources.Bitwarden => Bitwarden,
        _ => null,
    };

    public static string MissingText(string tool) =>
        $"Provenance needs the {Name(tool)} command-line tool. It can download its own copy from {Name(tool)}.";

    public static string RefusedText(string tool) =>
        $"Provenance can't use your copy of the {Name(tool)} command-line tool. It can download its own copy from {Name(tool)}.";

    public static string CantCheckText(string tool) =>
        $"Provenance couldn't check the {Name(tool)} command-line tool. It can download its own copy from {Name(tool)}.";

    public static string TooOldText(string tool) =>
        $"The {Name(tool)} command-line tool is too old for Provenance. It can download a newer copy from {Name(tool)}.";

    public static string DownloadingText(string tool) => $"Downloading the {Name(tool)} command-line tool...";

    public static string ReadyText(string tool) => $"The {Name(tool)} command-line tool is ready.";

    public static string ResultText(string tool, ToolInstall result) => result switch
    {
        ToolInstall.DownloadFailed => $"Couldn't download the {Name(tool)} command-line tool.",
        ToolInstall.CheckFailed => $"The download didn't check out as {Name(tool)}'s own, so Provenance deleted it.",
        ToolInstall.CantCheck => $"Provenance couldn't check the download from {Name(tool)}, so it deleted it.",
        ToolInstall.TooOld => $"The download from {Name(tool)} was an older version, so Provenance deleted it.",
        ToolInstall.NotSupported => $"{Name(tool)} doesn't make its command-line tool for this version of Windows.",
        _ => ReadyText(tool),
    };

    public static ToolFind Find(string tool)
    {
        if (FindForTest is { } fake) return fake(tool);
        var own = Path.Combine(Dir, tool + ".exe");
        return Find(tool, File.Exists(own) && PathSafety.OnLocalFixedDrive(own) ? own : null, SearchDirs(), PathSafety.IsAdminOnly,
                    (file, path, t) => CheckToRun(file, path, t, path == own));
    }

    private static Verdict CheckToRun(SafeFileHandle file, string path, string tool, bool own)
    {
        var verdict = Check(file, path, tool, online: false);
        if (verdict != Verdict.Genuine) return verdict;
        var version = own ? NotedVersion(Path.GetDirectoryName(path)!, tool) ?? VersionOf(path) : VersionOf(path);
        if (version is null) return Verdict.CantCheck;
        if (version >= Oldest[tool]) return Verdict.Genuine;
        Log.Warning("{Exe} in {Folder} is version {Version}, older than Provenance can use", tool + ".exe", Path.GetDirectoryName(path), version);
        return Verdict.TooOld;
    }

    // Provenance's own copy first. IsAdminOnly says no to anything in ProgramData, so for that one it's the SecureFiles
    // lock on the folder that does the job
    internal static ToolFind Find(string tool, string? own, IEnumerable<string> dirs, Func<string, bool> isAdminOnly,
                                  Func<SafeFileHandle, string, string, Verdict> check)
    {
        var exe = tool + ".exe";
        bool cantCheck = false, tooOld = false;
        if (own is not null && Open(own) is { } ownFile)
        {
            Verdict verdict;
            try { verdict = check(ownFile, own, tool); }
            catch { ownFile.Dispose(); throw; }
            if (verdict == Verdict.Genuine) return new ToolFind(own, null, handle: ownFile);
            ownFile.Dispose();
            if (verdict == Verdict.NotGenuine) DeleteOwn(own, tool, check);
            cantCheck |= verdict == Verdict.CantCheck;
            tooOld |= verdict == Verdict.TooOld;
        }
        string? refused = null;
        foreach (var dir in dirs)
        {
            SafeFileHandle? file = null;
            try
            {
                var candidate = Path.Combine(dir, exe);
                if (!File.Exists(candidate) || Open(candidate) is not { } opened) continue;
                file = opened;
                // a link's followed and the file it ends at is what runs. It has to be called op.exe or bw.exe too
                var real = RealPath(file);
                if (real is null || !string.Equals(Path.GetFileName(real), exe, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("{Candidate} isn't a file Provenance can follow, so it isn't used", candidate);
                    continue;
                }
                if (!isAdminOnly(real))
                {
                    Log.Information("{Exe} in {Folder} isn't admin-only, so it isn't used", exe, Path.GetDirectoryName(real));
                    refused ??= Path.GetDirectoryName(real);
                    continue;
                }
                var verdict = check(file, real, tool);
                if (verdict == Verdict.Genuine)
                {
                    var found = new ToolFind(real, null, handle: file);
                    file = null;
                    return found;
                }
                cantCheck |= verdict == Verdict.CantCheck;
                tooOld |= verdict == Verdict.TooOld;
                Log.Warning("{Exe} in {Folder} didn't pass the check ({Verdict}), so it isn't used", exe, Path.GetDirectoryName(real), verdict);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Log.Debug(ex, "Looking for {Exe} in {Folder} failed", exe, dir);
            }
            finally { file?.Dispose(); }
        }
        return new ToolFind(null, refused, cantCheck, tooOld: tooOld);
    }

    // checked again with the lock held, another window may have just put a good copy in
    private static void DeleteOwn(string own, string tool, Func<SafeFileHandle, string, string, Verdict> check)
    {
        try
        {
            using var held = AdminLock.TryAcquire(LockName(tool), TimeSpan.Zero);
            if (held is null) return;
            using (var file = Open(own))
                if (file is null || check(file, own, tool) != Verdict.NotGenuine) return;
            Log.Warning("Provenance's own {Exe} isn't signed by its maker, so it's deleted", tool + ".exe");
            TryDelete(own);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Debug(ex, "Could not delete {File}", own);
        }
    }

    // what the checked file says it is, op and bw have no version resource of their own. Kept per file for the run
    private static readonly Dictionary<string, (long Size, DateTime Written, Version? Version)> Versions = new(StringComparer.OrdinalIgnoreCase);

    internal static Version? VersionOf(string path)
    {
        var info = new FileInfo(path);
        lock (Versions)
            if (Versions.TryGetValue(path, out var seen) && seen.Size == info.Length && seen.Written == info.LastWriteTimeUtc) return seen.Version;
        if (ParseVersion(AskVersion(path)) is not { } version) return null;
        lock (Versions) Versions[path] = (info.Length, info.LastWriteTimeUtc, version);
        return version;
    }

    internal static Version? ParseVersion(string? line) => line is not null && VersionText.IsMatch(line) ? Version.Parse(line) : null;

    // no async in here, so it can't wait on a thread it's blocking. A version's one line, 4 KB is plenty
    private static string? AskVersion(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            };
            OtpSources.ToolEnvironment(psi.Environment);
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi) ?? throw new InvalidOperationException(Path.GetFileName(path) + " did not start.");
            p.StandardInput.Close();
            p.ErrorDataReceived += static (_, _) => { };
            p.BeginErrorReadLine();
            var read = Task.Run(() =>
            {
                var buf = new char[4096];
                int n = 0;
                for (int r; n < buf.Length && (r = p.StandardOutput.Read(buf, n, buf.Length - n)) > 0;) n += r;
                return new string(buf, 0, n);
            });
            if (!p.WaitForExit(30_000) || !read.Wait(5_000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                Log.Information("{File} didn't give its version in time", Path.GetFileName(path));
                return null;
            }
            if (p.ExitCode != 0) return null;
            return read.Result.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Information(ex, "Asking {File} for its version failed", Path.GetFileName(path));
            return null;
        }
    }

    private static SafeFileHandle? Open(string path)
    {
        try { return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static List<string> SearchDirs() =>
        ToolDirs(Environment.GetEnvironmentVariable("PATH"),
                 Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
                 Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 } pf64 ? Path.Combine(pf64, "WinGet", "Links") : "");

    // fully qualified only. A relative entry (".", "bin", "C:x", "\x") would resolve against whatever folder the app
    // happens to run in
    internal static List<string> ToolDirs(string? pathVariable, params string[] extra) =>
        (pathVariable ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Concat(extra)
            .Select(d => d.Trim().Trim('"'))
            .Where(d => d.Length > 0 && Path.IsPathFullyQualified(d))
            .ToList();

    // every link and junction on the way resolved. Null when it can't be opened (an app alias, say)
    internal static string? RealPath(string path)
    {
        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return RealPath(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? RealPath(SafeFileHandle file)
    {
        var buf = new char[512];
        uint n = GetFinalPathNameByHandle(file, buf, (uint)buf.Length, 0);
        if (n >= buf.Length)
        {
            buf = new char[n + 1];
            n = GetFinalPathNameByHandle(file, buf, (uint)buf.Length, 0);
        }
        if (n == 0 || n >= buf.Length) return null;
        var s = new string(buf, 0, (int)n);
        return s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + s[8..]
             : s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..]
             : s;
    }

    internal static Verdict Check(string path, string tool, bool online)
    {
        using var file = Open(path);
        return file is null ? Verdict.CantCheck : Check(file, path, tool, online);
    }

    // NotGenuine only when it's certain (changed, unsigned, the wrong signer, revoked). Anything it can't tell is
    // CantCheck, which never runs either
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
            OnePassword => Only(parts, "2.5.4.3", "Agilebits") && Only(parts, "2.5.4.10", "Agilebits") && HasEku(signer, OnePasswordEku),
            Bitwarden => Only(parts, "2.5.4.3", "Bitwarden Inc.") && Only(parts, "2.5.4.10", "Bitwarden Inc.")
                         && Only(parts, "2.5.4.5", BitwardenCompanyNumber),
            _ => false,
        };
    }

    // null for a multi-valued RDN, no real signer has one
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

    internal static (X509Certificate2 Root, string Ca) PinFor(string tool) => tool == OnePassword
        ? (MicrosoftRoot.Value, "Microsoft ID Verified Code Signing PCA 2021")
        : (DigiCertRoot.Value, "DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1");

    internal static PinResult Pinned(Signature sig, string tool, bool online)
    {
        var (root, ca) = PinFor(tool);
        return Pinned(sig.Chain[0], sig.Chain, root, ca, online, sig.StampedAt, sig.StampChain);
    }

    // only the root built in for that maker counts, through that maker's CA. Dates are as of a timestamp that pins
    // the same way, else now
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

    // revocation's only asked at install and update, when the PC's online anyway. An answer that doesn't come isn't
    // held against it, a revoked cert is
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

    // the signer, its chain and its timestamp exactly as WinVerifyTrust checked them, read through the handle that's held
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
            int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            try
            {
                var prov = WTHelperProvDataFromStateData(data.hWVTStateData);
                var signer = prov == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(prov, 0, false, 0);
                ReadChain(signer, chain);
                var stamp = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(prov, 0, true, 0);
                // CRYPT_PROVIDER_SGNR: a DWORD, then the FILETIME the timestamp gives. dwError comes after the chain
                // pointer, dwSignerType and psSigner
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
                WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            }
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
            if (added) file.DangerousRelease();
        }
    }

    // csCertChain sits at 12, after a DWORD and a FILETIME. Each CRYPT_PROVIDER_CERT is a DWORD, then the PCCERT_CONTEXT
    private static void ReadChain(IntPtr sgnr, X509Certificate2Collection into)
    {
        int count = sgnr == IntPtr.Zero ? 0 : Math.Min(Marshal.ReadInt32(sgnr, 12), 16);
        for (uint i = 0; i < count; i++)
        {
            var cert = WTHelperGetProvCertFromChain(sgnr, i);
            var context = cert == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(cert, IntPtr.Size);
            if (context == IntPtr.Zero) break;
            into.Add(new X509Certificate2(context));
        }
    }

    // test only, the app always goes through Check
    internal static Signature? VerifiedSignature(string path)
    {
        using var file = Open(path);
        return file is null ? null : WinVerify(file, path);
    }

    internal static bool Supported(string tool) => tool switch
    {
        OnePassword => OnePasswordArch() is not null,
        // x64 only, and Windows 10 on ARM can't run x64 programs
        Bitwarden => RuntimeInformation.OSArchitecture == Architecture.X64 ||
                     (RuntimeInformation.OSArchitecture == Architecture.Arm64 && Environment.OSVersion.Version.Build >= 22000),
        _ => false,
    };

    // there's no ARM64 build, the 32-bit one runs on both ARM Windows
    private static string? OnePasswordArch() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.X86 or Architecture.Arm64 => "386",
        _ => null,
    };

    // never throws, a cancel aside
    public static async Task<ToolInstall> InstallAsync(string tool, IProgress<double>? progress, CancellationToken ct)
    {
        if (!Supported(tool))
        {
            Log.Information("{Name}'s command-line tool isn't made for this Windows ({Arch})", Name(tool), RuntimeInformation.OSArchitecture);
            return ToolInstall.NotSupported;
        }
        IDisposable? held = null;
        try
        {
            held = await Task.Run(() => AdminLock.TryAcquire(LockName(tool), TimeSpan.FromMinutes(10), ct), ct);
            if (held is null) return ToolInstall.DownloadFailed;
            // another copy of Provenance may have put one in place while this one waited
            var own = Path.Combine(Dir, tool + ".exe");
            if (await Task.Run(() =>
                {
                    using var file = File.Exists(own) ? Open(own) : null;
                    return file is not null && CheckToRun(file, own, tool, own: true) == Verdict.Genuine;
                }, ct))
                return ToolInstall.Installed;
            var (version, url) = await LatestAsync(tool, ct);
            return await FetchAndSwapAsync(tool, version, url, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Downloading {Name}'s command-line tool failed", Name(tool));
            return ToolInstall.DownloadFailed;
        }
        finally { held?.Dispose(); }
    }

    // once a day at most, and only after the tool's been used. Never on a timer
    public static void UpdateInBackground(string tool) =>
        _ = Task.Run(async () =>
        {
            try { await UpdateIfDueAsync(tool, CancellationToken.None); }
            catch (Exception ex) { Log.Warning(ex, "Checking for a newer {Name} command-line tool failed", Name(tool)); }
        });

    internal static async Task UpdateIfDueAsync(string tool, CancellationToken ct)
    {
        var dir = Dir;
        if (!File.Exists(Path.Combine(dir, tool + ".exe"))) return;
        if (!Due(ReadState(dir, tool))) return;
        using var held = AdminLock.TryAcquire(LockName(tool), TimeSpan.Zero, ct);
        if (held is null) return;
        var state = ReadState(dir, tool);
        if (!Due(state)) return;
        // counts as checked even when the check fails, so a server that's down gets asked once a day, not every use
        WriteState(dir, tool, new ToolState(state?.Version ?? "", DateTime.UtcNow));
        var (version, url) = await LatestAsync(tool, ct);
        if (Version.TryParse(state?.Version, out var have) && Version.TryParse(version, out var latest) && latest <= have) return;
        var result = await FetchAndSwapAsync(tool, version, url, null, ct);
        Log.Information("{Name} command-line tool update to {Version}: {Result}", Name(tool), version, result);
    }

    private static bool Due(ToolState? state)
    {
        if (state is null) return true;
        var age = DateTime.UtcNow - state.Checked;
        return age < TimeSpan.Zero || age >= CheckEvery;   // a clock that went back counts as due
    }

    private static string LockName(string tool) => "cli-tool-" + tool;

    private static HttpClient Http => _http ??= Client(follow: true);
    private static HttpClient HttpNoRedirect => _httpNoRedirect ??= Client(follow: false);

    private static HttpClient Client(bool follow)
    {
        var c = _handlerForTest is { } h ? new HttpClient(h, disposeHandler: false)
              : new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = follow });
        c.Timeout = TimeSpan.FromSeconds(60);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Provenance");
        return c;
    }

    private static async Task<(string Version, string Url)> LatestAsync(string tool, CancellationToken ct)
    {
        if (tool == OnePassword)
        {
            // the same version check 1Password's own install script makes
            var arch = OnePasswordArch() ?? throw new PlatformNotSupportedException();
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(OnePasswordVersionUrl, ct));
            var v = doc.RootElement.GetProperty("version").GetString() ?? "";
            if (!VersionText.IsMatch(v)) throw new InvalidDataException("1Password's version check didn't give a version.");
            return (v, $"https://cache.agilebits.com/dist/1P/op2/pkg/v{v}/op_windows_{arch}_v{v}.zip");
        }
        // Bitwarden's download link redirects to the release's zip on GitHub, and that names the version
        using var r = await HttpNoRedirect.GetAsync(BitwardenDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        var location = r.Headers.Location is { IsAbsoluteUri: true } l ? l.AbsoluteUri : "";
        var m = BitwardenZip.Match(location);
        if (!m.Success) throw new InvalidDataException($"Bitwarden's download link answered {(int)r.StatusCode} with no release zip.");
        return (m.Groups[1].Value, location);
    }

    private static async Task<ToolInstall> FetchAndSwapAsync(string tool, string version, string url, IProgress<double>? progress,
                                                            CancellationToken ct)
    {
        var dir = Dir;
        Sweep(dir, tool);
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var zip = Path.Combine(dir, $"{tool}-new-{stamp}.zip");
        var exe = Path.Combine(dir, $"{tool}-new-{stamp}.exe");
        try
        {
            await DownloadAsync(url, zip, progress, ct);
            await Task.Run(() => ExtractExe(zip, tool + ".exe", exe), ct);
            // the check and the version run off one handle, held between them
            var (verdict, got) = await Task.Run(() =>
            {
                using var held = Open(exe);
                if (held is null) return (Verdict.CantCheck, (Version?)null);
                var v = Check(held, exe, tool, online: true);
                return (v, v == Verdict.Genuine ? VersionOf(exe) : null);
            }, ct);
            if (verdict != Verdict.Genuine)
            {
                Log.Warning("The {Name} command-line tool {Version} from {Url} didn't pass the signature check ({Verdict}), so it's deleted",
                            Name(tool), version, url, verdict);
                return verdict == Verdict.NotGenuine ? ToolInstall.CheckFailed : ToolInstall.CantCheck;
            }
            // the version the file itself gives, never what the server named. Nothing older than what's built in or
            // what's already there goes in
            var floor = File.Exists(Path.Combine(dir, tool + ".exe")) && NotedVersion(dir, tool) is { } have && have > Oldest[tool] ? have : Oldest[tool];
            if (got is null || got < floor)
            {
                Log.Warning("The {Name} command-line tool from {Url} says it's version {Got}, and nothing below {Floor} goes in, so it's deleted",
                            Name(tool), url, got, floor);
                return got is null ? ToolInstall.CantCheck : ToolInstall.TooOld;
            }
            Swap(dir, tool, exe);
            WriteState(dir, tool, new ToolState(got.ToString(), DateTime.UtcNow));
            Log.Information("Installed the {Name} command-line tool {Version}", Name(tool), got);
            return ToolInstall.Installed;
        }
        finally
        {
            TryDelete(zip);
            TryDelete(exe);
        }
    }

    // HttpClient's timeout only covers the headers, so each read gets its own limit
    private static async Task DownloadAsync(string url, string to, IProgress<double>? progress, CancellationToken ct)
    {
        using var r = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        r.EnsureSuccessStatusCode();
        long? total = r.Content.Headers.ContentLength;
        if (total > MaxZip) throw new InvalidDataException($"The download is {total} bytes, more than a command-line tool should be.");
        await using var src = await r.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buf = new byte[81920];
        long got = 0;
        while (true)
        {
            stall.CancelAfter(StallLimit);
            int n = await src.ReadAsync(buf, stall.Token);
            if (n == 0) break;
            got += n;
            if (got > MaxZip) throw new InvalidDataException("The download is bigger than a command-line tool should be.");
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            if (total > 0) progress?.Report(Math.Min(1, (double)got / total.Value));
        }
    }

    private static void ExtractExe(string zip, string name, string to)
    {
        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.Entries.SingleOrDefault(e => e.FullName == name)
                    ?? throw new InvalidDataException($"The download has no {name} in it.");
        if (entry.Length > MaxExe) throw new InvalidDataException($"{name} in the download is bigger than it should be.");
        using var src = entry.Open();
        using var dst = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buf = new byte[81920];
        long got = 0;
        for (int n; (n = src.Read(buf)) > 0;)
        {
            got += n;
            if (got > MaxExe) throw new InvalidDataException($"{name} in the download is bigger than it should be.");
            dst.Write(buf, 0, n);
        }
    }

    private static void Swap(string dir, string tool, string fresh)
    {
        var final = Path.Combine(dir, tool + ".exe");
        try { File.Move(fresh, final, overwrite: true); }
        catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && File.Exists(final))
        {
            // a copy that's running can't be replaced, but it can be moved out of the way
            File.Move(final, Path.Combine(dir, $"{tool}-old-{Guid.NewGuid().ToString("N")[..8]}.exe"));
            File.Move(fresh, final);
        }
    }

    // leftovers of a download that didn't finish, or of a swap. One that's still running stays till next time
    private static void Sweep(string dir, string tool)
    {
        foreach (var pattern in new[] { $"{tool}-new-*", $"{tool}-old-*" })
            foreach (var f in Directory.EnumerateFiles(dir, pattern)) TryDelete(f);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Debug(ex, "Could not delete {File}", path); }
    }

    private sealed record ToolState(string Version, DateTime Checked);

    private static string StatePath(string dir, string tool) => Path.Combine(dir, tool + ".json");

    private static ToolState? ReadState(string dir, string tool)
    {
        try { return JsonSerializer.Deserialize<ToolState>(File.ReadAllText(StatePath(dir, tool))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // what the file said when it went in, so the own copy isn't asked before every run
    private static Version? NotedVersion(string dir, string tool) => Version.TryParse(ReadState(dir, tool)?.Version, out var v) ? v : null;

    private static void WriteState(string dir, string tool, ToolState state)
    {
        var path = StatePath(dir, tool);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state));
        File.Move(tmp, path, overwrite: true);
    }

    // Microsoft Identity Verification Root Certificate Authority 2020, SHA-256 5367F20C...2E1270
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

    // DigiCert Trusted Root G4, SHA-256 552F7BDC...C89988
    private const string DigiCertTrustedRootG4 =
        "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" +
        "6hN35N6MgSGtBxBHEa2HPQfRdbzP82Z+";

    private const uint TrustBadDigest = 0x80096010, TrustNoSignature = 0x800B0100, TrustSubjectFormUnknown = 0x800B0003,
                       TrustExplicitDistrust = 0x800B0111, CertRevoked = 0x800B010C;

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint cert);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
}
