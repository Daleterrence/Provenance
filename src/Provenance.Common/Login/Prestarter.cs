using System.Diagnostics;
using Provenance.Common.Game;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Hook;
using Provenance.Common.Install;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Security;
using Provenance.Common.Settings;
using Serilog;

namespace Provenance.Common.Login;

public sealed class Prestarter
{
    internal const string PrestartLock = "prestart";
    private static long _dnsWarmedAt;

    internal sealed record Prestarted(int Pid, ProcessStamp? Stamp, DateTime? Began = null);

    internal sealed record Seams
    {
        public Func<bool>? GameIsInstalled { get; init; }
        public Func<Prestarted, bool>? PrestartAlive { get; init; }
        public Action? WarmDns { get; init; }
        public Func<Prestarted?>? Start { get; init; }
        public Func<bool, Prestarted?>? StartFor { get; init; }
        public TimeSpan? ReadyLimit { get; init; }
    }

    private readonly ISettingsService _settings;
    private readonly Watcher _watcher;
    private readonly OurPol _pol;
    private readonly Func<bool> _sessionActive;
    private readonly object _gate;
    private readonly Func<bool> _gameIsInstalled;
    private readonly Func<Prestarted, bool> _prestartAlive;
    private readonly Action _warmDns;
    private readonly Func<bool, Prestarted?> _start;
    private readonly TimeSpan? _readyLimit;
    private volatile bool _closed;
    private Task<Prestarted?>? _prestart;
    private int _prestartHolds;

    internal Prestarter(ISettingsService settings, Watcher watcher, OurPol pol, Func<bool> sessionActive, object gate, Seams? seams = null)
    {
        _settings = settings;
        _watcher = watcher;
        _pol = pol;
        _sessionActive = sessionActive;
        _gate = gate;
        _gameIsInstalled = seams?.GameIsInstalled ?? (() => GameInstalled(_settings.RegionOrDefault));
        // no recorded start time means no proof, so it's never mine (the user may run their own pol.exe on a reused pid)
        _prestartAlive = seams?.PrestartAlive ?? (pre => pre.Stamp is { } stamp && _pol.Ours(stamp));
        _warmDns = seams?.WarmDns ?? ResolveDns;
        _start = seams?.StartFor ?? (seams?.Start is { } plain ? _ => plain() : StartPrestarted);
        _readyLimit = seams?.ReadyLimit;
    }

    internal void Close() => _closed = true;

    internal bool Holding { get { lock (_gate) return _prestartHolds > 0; } }

    // started with the app to hide the 2s startup, never with Windower/Ashita as it'd open them
    public void Prestart(bool loginComing = false)
    {
        _warmDns();
        StartEarly(loginComing);
    }

    public void StartEarly(bool loginComing = false)
    {
        lock (_gate)
        {
            if (!MayPrestart()) return;
            _prestart = Task.Run(() => _start(loginComing));
        }
    }

    private bool MayPrestart() =>
        !_closed && _prestart == null && !_sessionActive() && _prestartHolds == 0
        && _settings.Current.ThirdPartyHookPlatform is not (ThirdPartyHook.Ashita or ThirdPartyHook.Windower)
        && PolPaths.Supported(_settings.DetectedRegion);

    private static void ResolveDns()
    {
        long now = Environment.TickCount64;
        if (_dnsWarmedAt != 0 && now - _dnsWarmedAt < 600_000) return;
        _dnsWarmedAt = now;
        _ = Task.Run(async () =>
        {
            try { await System.Net.Dns.GetHostAddressesAsync("ci000.pol.com").ConfigureAwait(false); }
            catch (System.Net.Sockets.SocketException) { }
        });
    }

    internal Prestarted? StartPrestarted(bool loginComing = false)
    {
        try
        {
            var (viewerDir, polExe, launcherExe) = _watcher.FindFiles();
            if (polExe is null || !_gameIsInstalled() || SignedBuilds.Check(viewerDir!) != ViewerBuild.State.Supported) return null;
            // patch.ini goes to startpol.exe /REPLACEPOLEXE, which starts a pol.exe that isn't mine
            if (ViewerQueue.QueuedWorkPending(viewerDir!))
            {
                Log.Information("Not pre-starting pol.exe: the PlayOnline Viewer has update work queued, the next login finishes it first");
                return null;
            }
            _watcher.BeforeStart(viewerDir!, "before the pre-start");
            var began = DateTime.UtcNow;
            var psi = Watcher.LauncherStart(launcherExe, polExe);
            HookEnvironment.SetPrestart(psi.Environment, loginComing);
            using var launcher = _watcher.StartProcess(psi);
            if (launcher is null || Watcher.RunLauncher(launcher, Watcher.Limit) is not { } run) return null;
            if (run.ExitCode != 0 || HookLog.StartedPid(run.Out) is not { } pid) return null;
            HookFiles.ClearHandoverFiles(pid);
            var stamp = ProcessStamp.Of(pid);
            if (stamp is { } at) HookFiles.NotePrestart(pid, at);
            Log.Information("Pre-started pol.exe (pid {Pid}), waiting for the login", pid);
            return new Prestarted(pid, stamp, began);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pre-start failed; the login starts pol.exe itself");
            return null;
        }
    }

    private static bool GameInstalled(PolRegion region) =>
        PolPaths.TryGetInstallFolder(region, GameProduct.Game) is { } ffxi && File.Exists(Path.Combine(ffxi, GameProduct.Game.KeyFile));

    internal int? TakePrestarted(ProcessStartInfo psi, CancellationToken ct)
    {
        if (TakePrestart(ct) is not { } pre) return null;
        int pid = pre.Pid;
        try
        {
            var log = HookFiles.PathFor(pid, HookContract.Files.Log);
            var until = (pre.Began ?? DateTime.UtcNow) + (_readyLimit ?? Watcher.Limit);
            while (true)
            {
                if (!_prestartAlive(pre)) return null;
                if (ct.IsCancellationRequested) { GiveBack(Task.FromResult<Prestarted?>(pre)); return null; }
                if (PrestartIsReady(log)) break;
                if (DateTime.UtcNow >= until) { EndPrestarted(pre); return null; }
                Thread.Sleep(15);
            }
            if (ct.IsCancellationRequested) { GiveBack(Task.FromResult<Prestarted?>(pre)); return null; }
            using (var handOver = AdminLock.TryAcquire(PrestartLock, TimeSpan.FromSeconds(5)))
            {
                if (handOver is null || File.Exists(HookFiles.PathFor(pid, HookContract.Files.Stop))) { EndPrestarted(pre); return null; }
                File.Delete(HookFiles.PathFor(pid, HookContract.Files.Prestart));
                HookEnvironment.WriteGoFile(pid, psi.Environment);
            }
            Log.Information("Using the pre-started pol.exe (pid {Pid})", pid);
            return pid;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { EndPrestarted(pre); return null; }
    }

    private Prestarted? TakePrestart(CancellationToken ct = default)
    {
        Task<Prestarted?>? task;
        lock (_gate) { task = _prestart; _prestart = null; }
        if (task is null) return null;
        try
        {
            if (task.Wait(8000, ct)) return task.Result;
        }
        catch (OperationCanceledException)
        {
            GiveBack(task);
            return null;
        }
        EndWhenDone(task);
        return null;
    }

    private void GiveBack(Task<Prestarted?> task)
    {
        lock (_gate)
        {
            if (MayPrestart())
            {
                _prestart = task;
                return;
            }
        }
        EndWhenDone(task);
    }

    private void EndWhenDone(Task<Prestarted?> task) =>
        task.ContinueWith(t => { if (t.Result is { } late) EndPrestarted(late); }, TaskContinuationOptions.OnlyOnRanToCompletion);

    private void EndPrestarted(Prestarted pre)
    {
        HookFiles.TryRequestStop(pre.Pid);
        HookFiles.Delete(pre.Pid, HookContract.Files.Prestart);
        if (pre.Stamp is { } stamp) _pol.End(stamp, graceMs: 3000);
    }

    public void StopPrestart()
    {
        try { if (TakePrestart() is { } pre) EndPrestarted(pre); }
        catch (AggregateException) { }
    }

    public IDisposable HoldPrestarts()
    {
        lock (_gate) _prestartHolds++;
        var hold = new Hold(this);
        try { FreeViewerFiles(); }
        catch
        {
            hold.Dispose();
            throw;
        }
        return hold;
    }

    private sealed class Hold(Prestarter owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release();
        }
    }

    internal void FreeViewerFiles()
    {
        StopPrestart();
        EndWaitingPrestarts();
    }

    internal void EndWaitingPrestarts()
    {
        var ending = new List<ProcessStamp>();
        try
        {
            using var handOver = AdminLock.TryAcquire(PrestartLock, TimeSpan.FromSeconds(5));
            if (handOver is null)
            {
                Log.Warning("Other copies' pre-started pol.exe were left running: the hand-over lock stayed busy");
                return;
            }
            foreach (var record in Directory.GetFiles(HookFiles.RequiredDir, "pol_hook.*.prestart"))
            {
                try
                {
                    var parts = Path.GetFileName(record).Split('.');
                    if (parts.Length != 3 || !int.TryParse(parts[1], out var pid)
                        || !long.TryParse(File.ReadAllText(record).Trim(), out var ticks)) continue;
                    var stamp = new ProcessStamp(pid, ticks);
                    if (_pol.Ours(stamp))
                    {
                        HookFiles.RequestStop(pid);
                        ending.Add(stamp);
                    }
                    File.Delete(record);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(e, "Could not end the pre-started pol.exe of {Record}", Path.GetFileName(record));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warning(e, "Could not end other copies' pre-started pol.exe");
        }
        foreach (var stamp in ending)
        {
            Log.Information("Ending another copy's pre-started pol.exe (pid {Pid}): the PlayOnline Viewer's files are about to change", stamp.Pid);
            _pol.End(stamp, graceMs: 3000);
        }
    }

    private void Release()
    {
        if (LetGoPrestarts()) Prestart();
    }

    internal bool LetGoPrestarts()
    {
        lock (_gate)
        {
            if (_prestartHolds == 0)
            {
                Log.Warning("Pre-starts were let go with none held");
                return false;
            }
            return --_prestartHolds == 0;
        }
    }

    private static bool PrestartIsReady(string logPath)
    {
        try { return HookLog.PrestartReadyInThisRun(logPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
