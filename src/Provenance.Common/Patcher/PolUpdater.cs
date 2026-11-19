using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using Provenance.Common.Game;
using Provenance.Common.IO;
using Provenance.Common.Install;
using Provenance.Common.PolFormat;
using Provenance.Common.Progress;
using Provenance.Common.Security;

namespace Provenance.Common.Patcher;

public sealed record VersionCheck(string Local, string Server, IPEndPoint? Endpoint)
{
    public bool UpToDate => PolVersion.UpToDate(Local, Server);

    public bool FromMirror => Endpoint is null;

    internal static VersionCheck Mirror(string local) => new(local, local, null);
}

public enum PatchResult { UpToDate, Updated, UpdatedQueued }

public enum PatchPhase { Check, Manifest, Scan, Download, Apply }

public sealed record PatchProgress(PatchPhase Phase, string Text, double Fraction);

// downloads get a few connections since one took over an hour on a fresh install
public sealed class PolUpdater(PatchProduct product, Func<string, bool>? only = null, int connections = PolUpdater.DefaultConnections)
{
    public PatchProduct Product => product;

    public const int DefaultConnections = 4;
    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;
    internal IPatchConnector Connector { get; init; } = DirectPatchConnector.Instance;
    private string Dir => product.Folder;

    // the app keeps Windower's Large Address Aware pol.exe, any other copy has to stay SE's to the byte
    public bool KeepLargeAddressAware { get; init; }

    internal string Local(string name) =>
        PathInside.Resolve(Dir, name) ?? throw new InvalidDataException($"The {product.Name} file list names an unsafe path: {name}");

    public bool NeedsUpdate(CancellationToken ct) => !Check(ct).UpToDate;

    public VersionCheck Check(CancellationToken ct) => Check(PatchVer.Read(product), ct);

    public VersionCheck Check(string local, CancellationToken ct)
    {
        product.CheckStream();
        var session = new PatchSession(product, Connector);
        var a = session.Connect(ct);
        var link = a.Link;
        var ep = new IPEndPoint(a.Address, product.Port);
        try
        {
            var r = session.AskVersion(link, local, ct);
            if (session.Screen(r, a.Address) == RedirectDecision.Reconnect && session.Follow(r, link, ct) is { } next)
            {
                link = next.Link;
                ep = new IPEndPoint(next.Address, product.Port);
            }
            if (r.Server.Length == 0) throw new InvalidDataException("PlayOnline's update server sent no version.");
            return new VersionCheck(local, r.Server, ep);
        }
        finally { link.Dispose(); }
    }

    public PatchResult Run(IProgress<PatchProgress>? progress, CancellationToken ct, Action? beforeChange = null, VersionCheck? check = null)
    {
        if (check == null)
        {
            progress?.Report(new PatchProgress(PatchPhase.Check, $"Checking for {product.Name} updates...", -1));
            check = Check(ct);
        }
        else product.CheckStream();
        var (local, server, _) = check;
        if (check.UpToDate) return PatchResult.UpToDate;
        var ep = check.Endpoint ?? throw new InvalidOperationException($"The {product.Name} version check didn't come from PlayOnline, so there's nowhere to download from.");
        beforeChange?.Invoke();

        var session = new PatchSession(product, Connector, pinned: ep.Address);
        using var link = session.Connect(ct).Link;
        progress?.Report(new PatchProgress(PatchPhase.Manifest, $"Getting the list of {product.Name} files...", -1));
        var manifest = FetchManifest(session, link, ct);
        if (only != null) manifest = manifest.Where(f => only(f.Name)).ToList();
        var decisions = Scan(manifest, manifestScan: true, local, progress, ct);
        var workIdx = Enumerable.Range(0, manifest.Count)
            .Where(i => decisions[i].Kind is Kind.Direct or Kind.Indirect).ToList();
        var work = workIdx.Select(i => manifest[i]).ToList();
        WriteText("patch2.cfg", PatchFileList.Write(work));
        if (work.Count == 0 && !decisions.Any(d => d.Kind == Kind.Current && d.FromTmp2))
        {
            PatchVer.Write(product, server);
            return PatchResult.UpToDate;
        }

        link.Dispose();
        if (work.Count > 0)
        {
            long total = workIdx.Sum(i => decisions[i].Bytes);
            long need = total + work.Sum(f => f.Last.Sig.Length) + (64L << 20);
            long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Dir))!).AvailableFreeSpace;
            if (free < need)
                throw Shown.Mark(new IOException($"The {product.Name} update needs about {ByteText.Mb(need, "N0")} MB of free space on the drive, but there's only {ByteText.Mb(free, "N0")} MB free. Free up some space, then try again."));
            Download(session, workIdx.Select(i => (manifest[i], decisions[i].Bytes)).ToList(), local, total, progress, ct);
        }

        progress?.Report(new PatchProgress(PatchPhase.Apply, $"Updating {product.Name}...", -1));
        return Apply(manifest, local, server);
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

    private List<PatchFile> FetchManifest(PatchSession session, PatchServerLink link, CancellationToken ct)
    {
        var body = session.FileList(link, ct);
        var tmp1 = Path.Combine(Dir, "patch.cfg.tmp1");
        var tmp2 = Path.Combine(Dir, "patch.cfg.tmp2");
        File.WriteAllBytes(tmp1, body.ToArray());
        using (var src = File.OpenRead(tmp1))
        using (var dst = new FileStream(tmp2, FileMode.Create, FileAccess.Write))
            PatchSession.UnpackFileList(src, dst, ct);
        File.Delete(tmp1);
        Replace(tmp2, Path.Combine(Dir, "patch.cfg"));
        AtomicFile.DeleteReadOnly(Path.Combine(Dir, "patch2.cfg"));
        return PatchFileList.Parse(File.ReadAllText(Path.Combine(Dir, "patch.cfg"), Encoding.Latin1), product.TagFilter);
    }

    private enum Kind { Skip, Current, Direct, Indirect }

    private readonly record struct Decision(Kind Kind, int From, long Bytes, bool FromTmp2);

    private Decision[] Scan(List<PatchFile> files, bool manifestScan, string local, IProgress<PatchProgress>? progress, CancellationToken ct)
    {
        var result = new Decision[files.Count];
        int done = 0;
        try
        {
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
        if (manifestScan && local.Length > 0 && PolVersion.Value(f.Last.Stamp) <= PolVersion.Value(local))
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
                if (fromTmp2) { AtomicFile.DeleteReadOnly(tmp2); continue; }   // POL's "illegal tmp2 file rollback"
                // Windower's Large Address Aware pol.exe is SE's file with one bit set
                if (KeepLargeAddressAware && f.Name.Equals(GameProduct.Viewer.KeyFile, StringComparison.OrdinalIgnoreCase) &&
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

    private void Download(PatchSession session, List<(PatchFile File, long Bytes)> work, string local, long total,
                          IProgress<PatchProgress>? progress, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (f, _) in work)
        {
            var full = Local(f.Name);
            if (Regex.IsMatch(full, @"\.tmp\d?$", RegexOptions.IgnoreCase) || !seen.Add(full))
                throw new InvalidDataException($"The {product.Name} file list names {f.Name} twice or as a temporary file.");
        }

        var workers = new DownloadWorkers(session, connections, product.Name, total, progress,
            (link, f, moved, kept, token) => FetchOne(session, link, f, local, moved, kept, token));
        workers.Run(work.OrderByDescending(w => w.Bytes).Select(w => w.File), ct);
    }

    private void FetchOne(PatchSession session, PatchServerLink link, PatchFile f, string local, Action<long> moved, Action kept, CancellationToken ct)
    {
        session.KeepAliveIfIdle(link, Clock(), ct);
        var d = Decide(f, manifestScan: false, local, ct);
        if (d.Kind == Kind.Direct) Direct(session, link, f, moved, ct);
        else if (d.Kind == Kind.Indirect) Indirect(session, link, f, d.From, d.FromTmp2, moved, kept, ct);
    }

    private sealed class DownloadWorkers(PatchSession session, int connections, string productName, long total, IProgress<PatchProgress>? progress,
                                         Action<PatchServerLink, PatchFile, Action<long>, Action, CancellationToken> fetch)
    {
        public void Run(IEnumerable<PatchFile> files, CancellationToken ct)
        {
            var queue = new ConcurrentQueue<PatchFile>(files);
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
                        $"Downloading {productName}: {ByteText.Mb(done, "0.0")} of {ByteText.Mb(total, "0.0")} MB", total > 0 ? Math.Clamp((double)done / total, 0, 1) : -1));
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
                            link ??= session.Connect(stop.Token).Link;
                            connecting = false;
                            fetch(link, f, Moved, Kept, stop.Token);
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
                throw new PatchServerNetworkException($"PlayOnline's update server stopped answering, so {queue.Count:N0} {productName} files weren't downloaded. Try again in a moment.", lastNetwork);
            Add(0, force: true);
        }
    }

    private static byte Fetch(PatchSession session, PatchServerLink link, string path, long size, string tmp, bool typeByte, Action<long> moved, CancellationToken ct)
    {
        using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        return session.Download(link, path, size, fs, typeByte, moved, ct);
    }

    private void Direct(PatchSession session, PatchServerLink link, PatchFile f, Action<long> moved, CancellationToken ct)
    {
        var target = Local(f.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string tmp = target + ".tmp", tmp2 = target + ".tmp2";
        byte type = Fetch(session, link, f.Last.SlcPath, f.Last.SlcLen, tmp, typeByte: true, moved, ct);
        PatchCodecs.DecodeSlc(type, tmp, tmp2, f.Last.Sig.Length, ct);
        File.Delete(tmp);
        Verify(tmp2, f.Last.Sig, $"{f.Name} failed its check after download.", ct);
    }

    private void Indirect(PatchSession session, PatchServerLink link, PatchFile f, int from, bool baseIsTmp2, Action<long> moved, Action stepKept, CancellationToken ct)
    {
        var target = Local(f.Name);
        string tmp = target + ".tmp", tmp2 = target + ".tmp2", tmp3 = target + ".tmp3";
        for (int i = from; i < f.Lines.Count; i++)
        {
            var line = f.Lines[i];
            Fetch(session, link, line.OlcPath!, line.OlcLen, tmp, typeByte: false, moved, ct);
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
            if (local.Length > 0 && PolVersion.Value(f.Last.Stamp) <= PolVersion.Value(local)) continue;
            var target = Local(f.Name);
            var tmp2 = target + ".tmp2";
            if (!File.Exists(tmp2)) continue;
            if (f.Attribute == BlockAttribute.Reset2)
            {
                if (!ViewerQueue.Queue(product.ViewerFolder, product.Folder.TrimEnd('\\') + "\\" + f.Name))
                    throw Shown.Mark(new IOException($"Provenance couldn't set {f.Name} to update when PlayOnline restarts. Try again."));
                rst.Append(f.Name).Append(' ');
                queued = true;
            }
            else Replace(tmp2, target);
        }
        PatchVer.Write(product, server);
        WriteText("patch.rst", rst.ToString());
        return queued ? PatchResult.UpdatedQueued : PatchResult.Updated;
    }

    // a new env.dat that moves the install between JP and US/EU means the wrong stream
    internal void CheckStagedEnv(List<PatchFile> manifest)
    {
        if (product.Game != GameProduct.Viewer) return;
        foreach (var rel in new[] { Path.Combine("EU", "env.dat"), Path.Combine("data", "utf", "env.dat") })
        {
            var staged = Path.Combine(Dir, rel) + ".tmp2";
            if (!File.Exists(staged)) continue;
            var area = PolEnv.Value(staged, "POL_UCS_AREA_KBN");
            if (area is "00" or "01" or "02" && (area == "00") == PatchProduct.IsJapan(product.Region)) continue;
            foreach (var f in manifest)
            {
                var tmp2 = Local(f.Name) + ".tmp2";
                if (File.Exists(tmp2)) AtomicFile.DeleteReadOnly(tmp2);
            }
            throw Shown.Mark(new InvalidDataException(
                $"PlayOnline's update server sent {product.Name} files for another version of PlayOnline, so none of them were installed."));
        }
    }

    private static void Replace(string staged, string target)
    {
        AtomicFile.ClearReadOnly(target);
        File.Move(staged, target, overwrite: true);
    }

    private void WriteText(string name, string text) => WriteBytes(name, Encoding.Latin1.GetBytes(text));

    private void WriteBytes(string name, byte[] bytes)
    {
        var path = Path.Combine(Dir, name);
        File.WriteAllBytes(path + ".provenance", bytes);
        Replace(path + ".provenance", path);
    }
}
