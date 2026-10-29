using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Patcher;

public sealed record PatchProduct(string Name, int Id, string Host, string TagWire, string TagFilter,
                                  string Folder, string ViewerFolder, string RegistryHive)
{
    public string ProductId => Id.ToString("0000");
    public int Port => 53000 + Id;

    // null reads the Interface value from the registry like POL. It's the same on every PC, so the publisher pins it
    public string? PatchVerInterface { get; init; }

    public PolRegion Region { get; init; } = PolRegion.UnitedStates;

    // pt004 and pt008 are one server: the viewer's wire tag (sqpolcts.bin) is what gets JP its own stream. FFXI's is
    // W20 everywhere, so every region gets the same FFXI
    public static PatchProduct Viewer(string viewerDir, PolRegion region = PolRegion.UnitedStates) =>
        new("PlayOnline Viewer", 1000, ViewerHost(viewerDir, region), Streams(region).Tag, Streams(region).Tag, viewerDir, viewerDir, PolPaths.Hive(region))
        { Region = region };

    public static PatchProduct Ffxi(string ffxiDir, string viewerDir, PolRegion region = PolRegion.UnitedStates) =>
        new("FINAL FANTASY XI", 1, $"pc001{Streams(region).Tag}.pol.com", "W20", Streams(region).Tag, ffxiDir, viewerDir, PolPaths.Hive(region))
        { Region = region };

    // US and EU share their streams, JP has its own
    private static (string ViewerHost, string Tag) Streams(PolRegion region) =>
        region == PolRegion.Japan ? ("pt004.pol.com", "W20") : ("pt008.pol.com", "W2U");

    // the other stream's host is never taken from env.dat
    internal static string ViewerHost(string viewerDir, PolRegion region)
    {
        var own = Streams(region).ViewerHost;
        var other = Streams(region == PolRegion.Japan ? PolRegion.UnitedStates : PolRegion.Japan).ViewerHost;
        var named = PolEnv.ValueIn(viewerDir, "PATCH_SERVER_DOMAIN");
        return named is null || !PolUpdater.IsPolHost(named) || named.Equals(other, StringComparison.OrdinalIgnoreCase) ? own : named;
    }

    internal static bool IsJapan(PolRegion? region) => region == PolRegion.Japan;

    // JP and US/EU files never mix, the US env.dat turns a JP install into a broken US one. Checked before a server's
    // asked anything
    public void CheckStream()
    {
        if (PolEnv.Region(ViewerFolder) is { } installed && IsJapan(installed) != IsJapan(Region))
            throw Shown.Mark(new InvalidOperationException(
                $"This PlayOnline install is the {PolPaths.VersionName(installed)} version, so it can't take {PolPaths.VersionName(Region)} {Name} files. Nothing was changed."));
        if (PolContents.WireTag(ViewerFolder, Id) is { } own && own != TagWire)
            throw Shown.Mark(new InvalidOperationException(
                $"The {PolPaths.VersionName(Region)} {Name} update doesn't match this PlayOnline install. Nothing was changed."));
    }
}

public sealed record VersionCheck(string Local, string Server, IPEndPoint Endpoint)
{
    public bool UpToDate => Base37.UpToDate(Local, Server);
}

public enum PatchResult { UpToDate, Updated, UpdatedQueued }

public enum PatchPhase { Check, Manifest, Scan, Download, Apply }

public sealed record PatchProgress(PatchPhase Phase, string Text, double Fraction);   // Fraction -1: nothing to measure

// app.dll's patch engine at 0x1029cd5a (driven by CUpgradeWindow at 0x101b68d4) request for request, except
// downloads get a few connections since one took over an hour on a fresh install
public sealed class PolUpdater(PatchProduct product, Func<string, bool>? only = null, int connections = PolUpdater.DefaultConnections)
{
    public const int DefaultConnections = 4;   // SE rate-limits when there are too many
    private const long KeepAliveMs = 60_000;
    private string Dir => product.Folder;

    // the app keeps Windower's Large Address Aware pol.exe, the publisher's copy has to stay SE's to the byte
    public bool KeepLargeAddressAware { get; init; }

    // the manifest's off the network and this runs as admin, so nothing gets to leave the product folder
    private string Local(string name)
    {
        var rel = name.Replace('/', '\\');
        if (rel.Length == 0 || Path.IsPathRooted(rel) || rel.Contains(':') || rel.Split('\\').Any(seg => seg is ".." or "."))
            throw new InvalidDataException($"The {product.Name} file list names an unsafe path: {name}");
        var root = Path.GetFullPath(Dir).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(Path.Combine(root, rel));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The {product.Name} file list names a path outside its folder: {name}");
        return full;
    }

    public bool NeedsUpdate(CancellationToken ct) => !Check(ct).UpToDate;

    public VersionCheck Check(CancellationToken ct) => Check(LocalVersion(), ct);

    public VersionCheck Check(string local, CancellationToken ct)
    {
        product.CheckStream();
        var ep = Resolve(ct);
        var link = PatchServerLink.Connect(ep, ct);
        try
        {
            var server = CheckVersion(ref link, ref ep, local, ct);
            return new VersionCheck(local, server, ep);
        }
        finally { link.Dispose(); }
    }

    // beforeChange only runs once an update's needed, and can throw to stop it
    public PatchResult Run(IProgress<PatchProgress>? progress, CancellationToken ct, Action? beforeChange = null, VersionCheck? check = null)
    {
        product.CheckStream();
        if (check == null)
        {
            progress?.Report(new PatchProgress(PatchPhase.Check, $"Checking for {product.Name} updates...", -1));
            check = Check(ct);
        }
        var (local, server, ep) = check;
        if (check.UpToDate) return PatchResult.UpToDate;
        beforeChange?.Invoke();

        // like POL, the manifest gets its own connection (mode 200), the downloads theirs (300) and apply (400) none
        using var link = PatchServerLink.Connect(ep, ct);
        progress?.Report(new PatchProgress(PatchPhase.Manifest, $"Getting the list of {product.Name} files...", -1));
        var manifest = FetchManifest(link, ct);
        if (only != null) manifest = manifest.Where(f => only(f.Name)).ToList();
        var decisions = Scan(manifest, manifestScan: true, local, progress, ct);
        var workIdx = Enumerable.Range(0, manifest.Count)
            .Where(i => decisions[i].Kind is Kind.Direct or Kind.Indirect).ToList();
        var work = workIdx.Select(i => manifest[i]).ToList();
        WriteText("patch2.cfg", PatchCfg.Write(work));
        if (work.Count == 0 && !decisions.Any(d => d.Kind == Kind.Current && d.FromTmp2))
        {
            WriteLocalVersion(server);
            return PatchResult.UpToDate;
        }

        link.Dispose();
        if (work.Count > 0)
        {
            long total = workIdx.Sum(i => decisions[i].Bytes);
            long need = total + work.Sum(f => f.Last.Sig.Length) + (64L << 20);
            long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Dir))!).AvailableFreeSpace;
            if (free < need)
                throw Shown.Mark(new IOException($"The {product.Name} update needs about {need / 1048576.0:N0} MB of free space on the drive, but there's only {free / 1048576.0:N0} MB free. Free up some space, then try again."));
            Download(workIdx.Select(i => (manifest[i], decisions[i].Bytes)).ToList(), ep, local, total, progress, ct);
        }

        progress?.Report(new PatchProgress(PatchPhase.Apply, $"Updating {product.Name}...", -1));
        return Apply(manifest, local, server);
    }

    public string LocalVersion()
    {
        var path = Path.Combine(Dir, "patch.ver");
        if (!File.Exists(path)) return "";
        var blob = File.ReadAllBytes(path);
        return PatchVer.Decode(blob, PatchVer.Key(product.Id, InterfaceValue()))
               ?? PatchVer.Decode(blob, PatchVer.Key(product.Id, null))
               ?? throw new InvalidDataException($"The {product.Name} version file (patch.ver) could not be read.");
    }

    // how POL reads it, with the Interface seed and no fallback. Null when that key doesn't open it
    public string? SeededVersion()
    {
        var path = Path.Combine(Dir, "patch.ver");
        return File.Exists(path) ? PatchVer.Decode(File.ReadAllBytes(path), PatchVer.Key(product.Id, InterfaceValue())) : null;
    }

    public void WriteLocalVersion(string version) =>
        WriteBytes("patch.ver", PatchVer.Encode(version, PatchVer.Key(product.Id, InterfaceValue())));

    private string? InterfaceValue() => product.PatchVerInterface ?? PatchVer.InterfaceValue(product.RegistryHive, product.Id);

    private IPEndPoint Resolve(CancellationToken ct)
    {
        var ip = Dns.GetHostAddressesAsync(product.Host, AddressFamily.InterNetwork, ct).GetAwaiter().GetResult().FirstOrDefault()
                 ?? throw Shown.Mark(new IOException("Couldn't find PlayOnline's update server. Check your internet connection, then try again."));
        return new IPEndPoint(ip, product.Port);
    }

    public static bool IsPolHost(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Any(char.IsWhiteSpace) || IPAddress.TryParse(host, out _)) return false;
        return host.Equals("pol.com", StringComparison.OrdinalIgnoreCase) ||
               (host.EndsWith(".pol.com", StringComparison.OrdinalIgnoreCase) && host.Length > ".pol.com".Length);
    }

    public enum RedirectDecision { None, Reconnect, Refuse }

    // only the IP it's already on (both products answer with it) or a pol.com host gets followed, this runs as admin
    public static RedirectDecision DecideRedirect(string s1, IPAddress current)
    {
        if (s1.Length == 0) return RedirectDecision.None;
        return s1 == current.ToString() || IsPolHost(s1) ? RedirectDecision.Reconnect : RedirectDecision.Refuse;
    }

    private string CheckVersion(ref PatchServerLink link, ref IPEndPoint ep, string local, CancellationToken ct)
    {
        var r = PatchProtocol.ParseVersion(link.Exchange(PatchProtocol.VersionRequest(product.TagWire, product.ProductId, local), ct));
        if (r.S0 == "unknown") throw new InvalidDataException($"PlayOnline's update server does not know {product.Name}.");
        var redirect = DecideRedirect(r.S1, ep.Address);
        if (redirect == RedirectDecision.Refuse) throw new InvalidDataException("PlayOnline's update server named an unexpected host.");
        if (redirect == RedirectDecision.Reconnect &&
            Dns.GetHostAddressesAsync(r.S1, AddressFamily.InterNetwork, ct).GetAwaiter().GetResult().FirstOrDefault() is { } other &&
            !other.Equals(IPAddress.Any))
        {
            ep = new IPEndPoint(other, product.Port);
            link.Dispose();
            link = PatchServerLink.Connect(ep, ct);
        }
        if (r.Server.Length == 0) throw new InvalidDataException("PlayOnline's update server sent no version.");
        return r.Server;
    }

    private void KeepAlive(PatchServerLink link, CancellationToken ct) =>
        PatchProtocol.ParseVersion(link.Exchange(PatchProtocol.VersionRequest(product.TagWire, product.ProductId, ""), ct));

    private List<PatchFile> FetchManifest(PatchServerLink link, CancellationToken ct)
    {
        var body = PatchProtocol.ParseManifest(link.Exchange(PatchProtocol.ManifestRequest(product.TagWire, product.ProductId), ct));
        var tmp1 = Path.Combine(Dir, "patch.cfg.tmp1");
        var tmp2 = Path.Combine(Dir, "patch.cfg.tmp2");
        File.WriteAllBytes(tmp1, body.ToArray());
        using (var src = File.OpenRead(tmp1))
        using (var dst = new FileStream(tmp2, FileMode.Create, FileAccess.Write))
            PatchCodecs.Lzss(src, dst, ct, 256L << 20);   // the biggest manifest is about 6 MB
        File.Delete(tmp1);
        Replace(tmp2, Path.Combine(Dir, "patch.cfg"));
        DeleteFile(Path.Combine(Dir, "patch2.cfg"));
        return PatchCfg.Parse(File.ReadAllText(Path.Combine(Dir, "patch.cfg"), Encoding.Latin1), product.TagFilter);
    }

    private enum Kind { Skip, Current, Direct, Indirect }

    private readonly record struct Decision(Kind Kind, int From, long Bytes, bool FromTmp2);

    private Decision[] Scan(List<PatchFile> files, bool manifestScan, string local, IProgress<PatchProgress>? progress, CancellationToken ct)
    {
        var result = new Decision[files.Count];
        int done = 0;
        try
        {
            // local hashing only, the network requests stay one at a time
            Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
                i =>
                {
                    result[i] = Decide(files[i], manifestScan, local, ct);
                    int n = Interlocked.Increment(ref done);
                    if (n % 64 == 0 || n == files.Count)
                        progress?.Report(new PatchProgress(PatchPhase.Scan, $"Checking {product.Name} files: {n:N0} of {files.Count:N0}", (double)n / files.Count));
                });
        }
        catch (AggregateException ae) when (ae.Flatten().InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Capture(ae.Flatten().InnerExceptions[0]).Throw();
        }
        return result;
    }

    private Decision Decide(PatchFile f, bool manifestScan, string local, CancellationToken ct)
    {
        if (manifestScan && local.Length > 0 && Base37.Value(f.Last.Stamp) <= Base37.Value(local))
            return new Decision(Kind.Skip, 0, 0, false);
        var target = Local(f.Name);
        var tmp2 = target + ".tmp2";
        while (true)
        {
            bool fromTmp2 = File.Exists(tmp2);
            if (!fromTmp2 && !File.Exists(target))
                return f.Attribute == BlockAttribute.Option ? new Decision(Kind.Skip, 0, 0, false) : DirectOf(f);
            var sig = FileSig.Of(fromTmp2 ? tmp2 : target, ct);
            int k = -1;
            for (int i = f.Lines.Count - 1; i >= 0; i--)
                if (f.Lines[i].Sig == sig) { k = i; break; }
            if (k == f.Lines.Count - 1) return new Decision(Kind.Current, 0, 0, fromTmp2);
            if (k < 0)
            {
                if (fromTmp2) { DeleteFile(tmp2); continue; }   // POL's "illegal tmp2 file rollback"
                // Windower's Large Address Aware pol.exe is SE's file with one bit set. A newer pol.exe replaces it whole
                if (KeepLargeAddressAware && f.Name.Equals("pol.exe", StringComparison.OrdinalIgnoreCase) &&
                    LargeAddressAware.Unpatched(target) is { } se && FileSig.Of(new MemoryStream(se), ct) == f.Last.Sig)
                    return new Decision(Kind.Current, 0, 0, false);
                return DirectOf(f);
            }
            long indirect = 0;
            for (int i = k + 1; i < f.Lines.Count; i++)
            {
                if (f.Lines[i].OlcPath is null || f.Lines[i].OlcLen == 0) { indirect = 0; break; }
                indirect += f.Lines[i].OlcLen;
            }
            if (indirect == 0 || local.Length == 0 || f.Last.SlcLen <= indirect) return DirectOf(f);
            return new Decision(Kind.Indirect, k + 1, indirect, fromTmp2);
        }
    }

    private static Decision DirectOf(PatchFile f) => new(Kind.Direct, 0, f.Last.SlcLen, false);

    // Biggest files first. A broken connection gets replaced and its file restarted (three goes max), a refused one
    // hands its file back, and whatever's left gets one single-connection round.
    private void Download(List<(PatchFile File, long Bytes)> work, IPEndPoint ep, string local, long total,
                          IProgress<PatchProgress>? progress, CancellationToken ct)
    {
        // workers can't share a target or its .tmp files, and Windows folds case and drops trailing dots, so full paths
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (f, _) in work)
        {
            var full = Local(f.Name);
            if (Regex.IsMatch(full, @"\.tmp\d?$", RegexOptions.IgnoreCase) || !seen.Add(full))
                throw new InvalidDataException($"The {product.Name} file list names {f.Name} twice or as a temporary file.");
        }

        var queue = new ConcurrentQueue<PatchFile>(work.OrderByDescending(w => w.Bytes).Select(w => w.File));
        var attempts = new ConcurrentDictionary<string, int>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long done = 0, lastReport = 0;
        Exception? failure = null, lastNetwork = null;
        var gate = new object();
        void Add(long n, bool force = false)
        {
            lock (gate)
            {
                done += n;
                if (!force && done < total && Environment.TickCount64 - lastReport < 250) return;
                lastReport = Environment.TickCount64;
                progress?.Report(new PatchProgress(PatchPhase.Download,
                    $"Downloading {product.Name}: {done / 1048576.0:0.0} of {total / 1048576.0:0.0} MB", total > 0 ? Math.Clamp((double)done / total, 0, 1) : -1));
            }
        }
        void Fail(Exception e)
        {
            Interlocked.CompareExchange(ref failure, e, null);
            stop.Cancel();
        }
        void Worker()
        {
            PatchServerLink? link = null;
            try
            {
                while (!stop.IsCancellationRequested && queue.TryDequeue(out var f))
                {
                    long counted = 0, kept = 0;
                    void Moved(long n) { counted += n; Add(n); }
                    void Kept() => kept = counted;
                    bool connecting = link is null;
                    try
                    {
                        link ??= PatchServerLink.Connect(ep, stop.Token);
                        connecting = false;
                        if (Environment.TickCount64 - link.LastRequestTicks > KeepAliveMs) KeepAlive(link, stop.Token);
                        var d = Decide(f, manifestScan: false, local, stop.Token);
                        if (d.Kind == Kind.Direct) Direct(link, f, Moved, stop.Token);
                        else if (d.Kind == Kind.Indirect) Indirect(link, f, d.From, d.FromTmp2, Moved, Kept, stop.Token);
                    }
                    catch (PatchServerNetworkException e) when (!stop.IsCancellationRequested)
                    {
                        Add(kept - counted);
                        link?.Dispose();
                        link = null;
                        lastNetwork = e;
                        if (!connecting && attempts.AddOrUpdate(f.Name, 1, (_, n) => n + 1) >= 3) { Fail(e); return; }
                        queue.Enqueue(f);
                        if (connecting) return;
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) { Fail(e); }
            finally { link?.Dispose(); }
        }
        void Round(int workers)
        {
            var started = new List<Thread>();
            try
            {
                for (int i = 0; i < workers; i++)
                {
                    var t = new Thread(Worker) { IsBackground = true, Name = $"update {i}" };
                    t.Start();
                    started.Add(t);
                }
            }
            catch
            {
                stop.Cancel();
                started.ForEach(t => t.Join());
                throw;
            }
            started.ForEach(t => t.Join());
        }

        Round(Math.Max(1, connections));
        if (failure == null && !ct.IsCancellationRequested && !queue.IsEmpty) Round(1);
        ct.ThrowIfCancellationRequested();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        if (!queue.IsEmpty)
            throw new PatchServerNetworkException($"PlayOnline's update server stopped answering, so {queue.Count:N0} {product.Name} files weren't downloaded. Try again in a moment.", lastNetwork);
        Add(0, force: true);
    }

    private byte Fetch(PatchServerLink link, string path, long size, string tmp, bool typeByte, Action<long> moved, CancellationToken ct)
    {
        byte type = 0;
        using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        for (long got = 0; got < size;)
        {
            ct.ThrowIfCancellationRequested();
            int len = (int)Math.Min(size - got, PatchProtocol.MaxRange);
            var data = PatchProtocol.ParseRange(link.Exchange(PatchProtocol.RangeRequest(got, len, product.TagWire, product.ProductId, path), ct), len).Span;
            if (got == 0 && typeByte) { type = data[0]; data = data[1..]; }
            fs.Write(data);
            got += len;
            moved(len);
        }
        return type;
    }

    private void Direct(PatchServerLink link, PatchFile f, Action<long> moved, CancellationToken ct)
    {
        var target = Local(f.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string tmp = target + ".tmp", tmp2 = target + ".tmp2";
        byte type = Fetch(link, f.Last.SlcPath, f.Last.SlcLen, tmp, typeByte: true, moved, ct);
        PatchCodecs.DecodeSlc(type, tmp, tmp2, f.Last.Sig.Length, ct);
        File.Delete(tmp);
        Verify(tmp2, f.Last.Sig, $"{f.Name} failed its check after download.", ct);
    }

    private void Indirect(PatchServerLink link, PatchFile f, int from, bool baseIsTmp2, Action<long> moved, Action stepKept, CancellationToken ct)
    {
        var target = Local(f.Name);
        string tmp = target + ".tmp", tmp2 = target + ".tmp2", tmp3 = target + ".tmp3";
        for (int i = from; i < f.Lines.Count; i++)
        {
            var line = f.Lines[i];
            Fetch(link, line.OlcPath!, line.OlcLen, tmp, typeByte: false, moved, ct);
            PatchCodecs.ApplyOlc(tmp, i == from && !baseIsTmp2 ? target : tmp2, tmp3, line.Sig.Length, ct);
            File.Delete(tmp);
            File.Move(tmp3, tmp2, overwrite: true);
            Verify(tmp2, line.Sig, $"{f.Name} failed its check after an update step.", ct);
            stepKept();
        }
    }

    private static void Verify(string tmp2, FileSig sig, string message, CancellationToken ct)
    {
        if (FileSig.Of(tmp2, ct) == sig) return;
        File.Delete(tmp2);
        throw new InvalidDataException(message);
    }

    private PatchResult Apply(List<PatchFile> manifest, string local, string server)
    {
        CheckStagedEnv(manifest);
        bool queued = false;
        var rst = new StringBuilder();
        foreach (var f in manifest)
        {
            if (local.Length > 0 && Base37.Value(f.Last.Stamp) <= Base37.Value(local)) continue;
            var target = Local(f.Name);
            var tmp2 = target + ".tmp2";
            if (!File.Exists(tmp2)) continue;
            if (f.Attribute == BlockAttribute.Reset2)
            {
                // files a running viewer has open get queued in its patch.ini for startpol.exe
                var key = product.Folder.TrimEnd('\\') + "\\" + f.Name;
                if (!WritePrivateProfileStringA("Rename", key, key + ".tmp2", Path.Combine(product.ViewerFolder, "patch.ini")))
                    throw Shown.Mark(new IOException($"Provenance couldn't set {f.Name} to update when PlayOnline restarts. Try again."));
                rst.Append(f.Name).Append(' ');
                queued = true;
            }
            else Replace(tmp2, target);
        }
        WriteLocalVersion(server);
        WriteText("patch.rst", rst.ToString());
        return queued ? PatchResult.UpdatedQueued : PatchResult.Updated;
    }

    // the last check before anything's replaced: a new env.dat that moves the install between JP and US/EU means the
    // wrong stream, so nothing goes in
    internal void CheckStagedEnv(List<PatchFile> manifest)
    {
        if (product.Id != 1000) return;
        foreach (var rel in new[] { Path.Combine("EU", "env.dat"), Path.Combine("data", "utf", "env.dat") })
        {
            var staged = Path.Combine(Dir, rel) + ".tmp2";
            if (!File.Exists(staged)) continue;
            var area = PolEnv.Value(staged, "POL_UCS_AREA_KBN");
            if (area is "00" or "01" or "02" && (area == "00") == PatchProduct.IsJapan(product.Region)) continue;
            foreach (var f in manifest)
            {
                var tmp2 = Local(f.Name) + ".tmp2";
                if (File.Exists(tmp2)) DeleteFile(tmp2);
            }
            throw Shown.Mark(new InvalidDataException(
                $"PlayOnline's update server sent {product.Name} files for another version of PlayOnline, so none of them were installed."));
        }
    }

    private static void Replace(string staged, string target)
    {
        Writable(target);
        File.Move(staged, target, overwrite: true);
    }

    private static void DeleteFile(string path)
    {
        Writable(path);
        File.Delete(path);
    }

    private static void Writable(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
    }

    private void WriteText(string name, string text) => WriteBytes(name, Encoding.Latin1.GetBytes(text));

    private void WriteBytes(string name, byte[] bytes)
    {
        var path = Path.Combine(Dir, name);
        File.WriteAllBytes(path + ".provenance", bytes);
        Replace(path + ".provenance", path);
    }

    // The viewer's next start, done here: startpol.exe /REPLACEPOLEXE moves queued .tmp2 files in (patch.ini ->
    // patch.bak), then pol.exe's boot step at 0x00410710 refreshes the DLLs. Throws if any are in use.
    public static void ApplyQueued(string viewerDir)
    {
        var ini = Path.Combine(viewerDir, "patch.ini");
        if (File.Exists(ini))
        {
            var rename = File.ReadAllLines(ini, Encoding.Latin1)
                .SkipWhile(l => !l.Trim().Equals("[Rename]", StringComparison.OrdinalIgnoreCase)).Skip(1)
                .TakeWhile(l => !l.TrimStart().StartsWith('['));
            foreach (var line in rename)
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string target = line[..eq].Trim(), staged = line[(eq + 1)..].Trim();
                if (!IsInside(viewerDir, target) || !IsInside(viewerDir, staged))
                    throw new InvalidDataException($"patch.ini names a file outside the PlayOnline Viewer folder: {target}");
                if (!File.Exists(staged)) continue;
                Writable(target);
                File.Copy(staged, target, overwrite: true);
                DeleteFile(staged);
            }
            Writable(Path.Combine(viewerDir, "patch.bak"));
            File.Copy(ini, Path.Combine(viewerDir, "patch.bak"), overwrite: true);
            DeleteFile(ini);
        }
        foreach (var (staged, live) in BootCopies(viewerDir))
        {
            Writable(live);
            File.Copy(staged, live, overwrite: true);
        }
    }

    public static bool QueuedWorkPending(string viewerDir) =>
        File.Exists(Path.Combine(viewerDir, "patch.ini")) || BootCopies(viewerDir).Any();

    // pol.exe's boot list (wide strings at 0x453134) and its test (0x004073d0). That only compares write times (of
    // course it does), so a different time makes pol.exe copy on every start, and fail if a DLL is held.
    private static IEnumerable<(string Staged, string Live)> BootCopies(string viewerDir) =>
        from name in new[] { "app.dll", "appEU.dll", "polcore.dll", "polcoreEU.dll" }
        let staged = Path.Combine(viewerDir, "patchfiles", "PlayOnlineViewer", "viewer", "com", name)
        let live = Path.Combine(viewerDir, "viewer", "com", name)
        where File.Exists(staged)
              && (!File.Exists(live) || File.GetLastWriteTimeUtc(staged) != File.GetLastWriteTimeUtc(live))
        select (staged, live);

    private static bool IsInside(string dir, string path)
    {
        var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrivateProfileStringA(string section, string key, string value, string file);
}
