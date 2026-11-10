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
    private const string PrestartLock = "prestart";
    private static long _dnsWarmedAt;

    internal sealed record Prestarted(int Pid, ProcessStamp? Stamp);

    internal sealed record Seams
    {
        public Func<bool>? GameIsInstalled { get; init; }
        public Func<Prestarted, bool>? PrestartAlive { get; init; }
        public Action? WarmDns { get; init; }
        public Func<Prestarted?>? Start { get; init; }
    }

    private readonly ISettingsService _settings;
    private readonly Watcher _watcher;
    private readonly OurPol _pol;
    private readonly Func<bool> _sessionActive;
    private readonly object _gate;
    private readonly Func<bool> _gameIsInstalled;
    private readonly Func<Prestarted, bool> _prestartAlive;
    private readonly Action _warmDns;
    private readonly Func<Prestarted?> _start;
    private volatile bool _closed;
    private Task<Prestarted?>? _prestart;
    private int _prestartHolds;

    // the gate is the session's, so a pre-start and a session starting can't miss each other
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
        _start = seams?.Start ?? StartPrestarted;
    }

    internal void Close() => _closed = true;

    // pol.exe and polcore take about 2s to start, so one starts with the app and waits for its go file. Not with
    // Windower/Ashita, it'd open them.
    public void Prestart()
    {
        _warmDns();
        lock (_gate)
        {
            if (!MayPrestart()) return;
            _prestart = Task.Run(_start);
        }
    }

    private bool MayPrestart() =>
        _prestart == null && !_sessionActive() && _prestartHolds == 0
        && _settings.Current.ThirdPartyHookPlatform is not (ThirdPartyHook.Ashita or ThirdPartyHook.Windower)
        && PolPaths.Supported(_settings.DetectedRegion);

    // only the resolver sees this, nothing connects to POL
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

    internal Prestarted? StartPrestarted()
    {
        try
        {
            var (viewerDir, polExe, launcherExe) = _watcher.FindFiles();
            // no network here, the sign-in does the version check. With no game there's no sign-in card to use it, and
            // the install would take it for POL running
            if (polExe is null || !_gameIsInstalled() || SignedBuilds.Check(viewerDir!) != ViewerBuild.State.Supported) return null;
            // a queued update is the login's job, pol.exe would act on it itself (patch.ini goes to startpol.exe
            // /REPLACEPOLEXE, which starts a pol.exe that isn't mine)
            if (ViewerQueue.QueuedWorkPending(viewerDir!))
            {
                Log.Information("Not pre-starting pol.exe: the viewer has update work queued, the next login finishes it first");
                return null;
            }
            _watcher.BeforeStart(viewerDir!, "before the pre-start");
            var psi = Watcher.LauncherStart(launcherExe, polExe);
            HookEnvironment.SetPrestart(psi.Environment);
            using var launcher = _watcher.StartProcess(psi);
            if (launcher is null || Watcher.RunLauncher(launcher, Watcher.Limit) is not { } run) return null;
            if (run.ExitCode != 0 || HookLog.StartedPid(run.Out) is not { } pid) return null;
            // an older pol.exe with this pid left these, and its stop file would end this one before it logs in
            foreach (var name in new[] { HookContract.Files.Stop, HookContract.Files.Ended, HookContract.Files.Go }) HookFiles.Delete(pid, name);
            var stamp = ProcessStamp.Of(pid);
            // so another copy that's about to change the viewer's files can end it while it's only waiting
            if (stamp is { } at)
                try { File.WriteAllText(HookFiles.PathFor(pid, HookContract.Files.Prestart), at.StartUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warning(e, "Could not note the pre-started pol.exe for other copies"); }
            Log.Information("Pre-started pol.exe (pid {Pid}), waiting for LOG IN", pid);
            return new Prestarted(pid, stamp);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pre-start failed; LOG IN starts pol.exe itself");
            return null;
        }
    }

    private static bool GameInstalled(PolRegion region) =>
        PolPaths.TryGetInstallFolder(region, GameProduct.Game) is { } ffxi && File.Exists(Path.Combine(ffxi, GameProduct.Game.KeyFile));

    // nothing's sent till the go file's written, so an earlier cancel leaves it waiting, hidden, for the next login.
    // Only a live one goes back (a dead one would block a Prestart)
    internal int? TakePrestarted(ProcessStartInfo psi, CancellationToken ct)
    {
        if (TakePrestart(ct) is not { } pre) return null;
        int pid = pre.Pid;
        try
        {
            if (!_prestartAlive(pre)) return null;
            if (ct.IsCancellationRequested) { GiveBack(Task.FromResult<Prestarted?>(pre)); return null; }
            var log = HookFiles.PathFor(pid, HookContract.Files.Log);
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < until && !PrestartIsReady(log) && !ct.IsCancellationRequested) Thread.Sleep(100);
            if (ct.IsCancellationRequested) { GiveBack(Task.FromResult<Prestarted?>(pre)); return null; }
            if (!PrestartIsReady(log)) { EndPrestarted(pre); return null; }
            // under the lock another copy ends waiting pre-starts with, so it's either been stopped or it's mine to use
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

    // kept for the next login, unless another one, a session or a hold has turned up meanwhile
    private void GiveBack(Task<Prestarted?> task)
    {
        lock (_gate)
        {
            if (!_closed && MayPrestart())
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
        try { HookFiles.RequestStop(pre.Pid); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        HookFiles.Delete(pre.Pid, HookContract.Files.Prestart);
        if (pre.Stamp is { } stamp) _pol.End(stamp, graceMs: 3000);
    }

    public void StopPrestart()
    {
        try { if (TakePrestart() is { } pre) EndPrestarted(pre); }
        catch (AggregateException) { }
    }

    // a repair, an install or a Large Address Aware write changes viewer files, so nothing pre-starts pol.exe until
    // every hold's let go (a sign-out's own Prestart included). They nest
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

    // other copies' waiting pre-starts hold the viewer's files too (their next login just starts another).
    // One being handed to a login has already lost its record by then
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
            Log.Information("Ending another copy's pre-started pol.exe (pid {Pid}): the viewer's files are about to change", stamp.Pid);
            _pol.End(stamp, graceMs: 3000);
        }
    }

    private void Release()
    {
        if (LetGoPrestarts()) Prestart();
    }

    // true when that was the last hold
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
