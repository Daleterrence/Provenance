using Provenance.Common.Game;
using Provenance.Common.Hook;
using Provenance.Common.Progress;
using Provenance.Common.Security;
using Serilog;

namespace Provenance.Common.Login;

// the viewer check has the status line to itself, so the start's lines wait here till it's answered
internal sealed class HeldProgress(IProgress<LaunchProgress>? to)
{
    private readonly object _gate = new();
    private LaunchProgress? _line, _notice;
    private bool _released, _dropped;

    internal void Report(LaunchProgress p)
    {
        lock (_gate)
        {
            if (_dropped) return;
            if (_released) to?.Report(p);
            else if (p.Kind == LaunchProgressKind.Notice) _notice = p;
            else _line = p;
        }
    }

    internal void Release(bool withLine = true)
    {
        lock (_gate)
        {
            if (_released || _dropped) return;
            _released = true;
            if (withLine && _line is { } line) to?.Report(line);
            if (_notice is { } notice) to?.Report(notice);
        }
    }

    internal void Drop()
    {
        lock (_gate) _dropped = true;
    }
}

// pol.exe started through Windower or Ashita while the viewer check's still out, waiting for its go file. The chain
// gets a thread of its own, Windower's turn belongs to the thread that took it
internal sealed class EarlyStart
{
    internal delegate int? Chain(IProgress<LaunchProgress> progress, CancellationToken ct, Action<ProcessStamp> hookStarted);

    private readonly Chain _chain;
    private readonly HeldProgress _voice;
    private readonly OurPol _pol;
    private readonly Action<ProcessStamp> _found;
    private readonly Action _ended;
    private readonly TimeSpan _grace;
    private readonly string _noStartText;
    private readonly CancellationTokenSource _cts;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _done = new();
    private readonly object _endGate = new();
    private int _state;   // 0 running, 1 handed over, 2 ended
    private bool _chainClosed, _closed;
    private ProcessStamp? _stamp, _hook;
    private int? _pid;
    private string? _failure;

    internal EarlyStart(Chain chain, HeldProgress voice, OurPol pol, Action<ProcessStamp> found, Action ended, TimeSpan grace,
                        string noStartText, CancellationToken ct)
    {
        _chain = chain;
        _voice = voice;
        _pol = pol;
        _found = found;
        _ended = ended;
        _grace = grace;
        _noStartText = noStartText;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _thread = new Thread(Run) { IsBackground = true, Name = "pol.exe early start" };
    }

    internal int? Pid => _pid;

    internal ProcessStamp? Hook => _hook;

    internal string? Failure => Volatile.Read(ref _failure);

    internal void Start() => _thread.Start();

    private void Run()
    {
        try
        {
            if (_chain(new InlineProgress<LaunchProgress>(Report), _cts.Token, hook => _hook = hook) is { } pid) Found(pid);
            else if (!_cts.IsCancellationRequested) _failure ??= _noStartText;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Starting pol.exe early failed");
            _failure ??= ErrorText.Of(ex, _noStartText);
        }
        finally { _done.Set(); }
    }

    private void Report(LaunchProgress p)
    {
        switch (p.Kind)
        {
            case LaunchProgressKind.Failed:
                _failure = p.Detail;
                break;
            case LaunchProgressKind.Stopped:
                _chainClosed |= p.Detail == LaunchProgress.Cancelled(polClosed: true).Detail;
                break;
            default:
                _voice.Report(p);
                break;
        }
    }

    // an old process with this pid may have left a stop or go file, either would end this one or send it off early
    private void Found(int pid)
    {
        foreach (var name in new[] { HookContract.Files.Stop, HookContract.Files.Ended, HookContract.Files.Go }) HookFiles.Delete(pid, name);
        if (ProcessStamp.Of(pid) is not { } stamp)
        {
            _failure = _noStartText;
            return;
        }
        _pid = pid;
        _stamp = stamp;
        // so another copy that's about to change the viewer's files can end it while it's only waiting
        try { File.WriteAllText(HookFiles.PathFor(pid, HookContract.Files.Prestart), stamp.StartUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warning(e, "Could not note the early pol.exe for other copies"); }
        _found(stamp);
        Log.Information("Started pol.exe (pid {Pid}) ahead of the viewer check, waiting for the login", pid);
    }

    // false when the cancel came first
    internal bool Wait(CancellationToken ct)
    {
        try { _done.Wait(ct); return true; }
        catch (OperationCanceledException) { return false; }
    }

    // under the lock another copy ends waiting pol.exe with, so it's either been stopped or it's mine to use
    internal bool HandOver(IDictionary<string, string?> env)
    {
        if (_pid is not { } pid || _stamp is not { } stamp) return false;
        try
        {
            using var handOver = AdminLock.TryAcquire(Prestarter.PrestartLock, TimeSpan.FromSeconds(5));
            if (handOver is null || File.Exists(HookFiles.PathFor(pid, HookContract.Files.Stop)) || !_pol.Ours(stamp)) return false;
            File.Delete(HookFiles.PathFor(pid, HookContract.Files.Prestart));
            HookEnvironment.WriteGoFile(pid, env);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warning(e, "Could not hand the early pol.exe its login");
            return false;
        }
        lock (_endGate) _state = 1;
        _cts.Dispose();
        return true;
    }

    // stops the chain, waits for it to unwind, then ends what it started. True when a pol.exe or hook was closed
    internal bool End()
    {
        lock (_endGate)
        {
            if (_state != 0) return _closed;
            _state = 2;
        }
        _cts.Cancel();
        _done.Wait();
        bool closed = _chainClosed;
        if (_pid is { } pid)
        {
            try { HookFiles.RequestStop(pid); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warning(e, "Could not ask the early pol.exe to stop"); }
            HookFiles.Delete(pid, HookContract.Files.Prestart);
            if (_stamp is { } stamp)
            {
                closed |= stamp.IsRunning();
                _pol.End(stamp, graceMs: (int)_grace.TotalMilliseconds);
                if (!stamp.IsRunning()) HookFiles.Delete(pid, HookContract.Files.Stop);
            }
        }
        ThirdPartyHookTracker.End(_hook);
        _ended();
        _cts.Dispose();
        _closed = closed;
        return closed;
    }
}
