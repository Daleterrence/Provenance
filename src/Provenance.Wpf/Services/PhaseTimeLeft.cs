using System.Windows.Threading;
using Provenance.Common.Patcher;
using Provenance.Common.Progress;

namespace Provenance.Wpf.Services;

public sealed class PhaseTimeLeft
{
    private const long Whole = 1_000_000;

    private readonly TimeLeft _left = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Action<string> _show;
    private PatchPhase? _phase;
    private double _held;
    private long _done;

    public PhaseTimeLeft(Action<string> show)
    {
        _show = show;
        _timer.Tick += (_, _) =>
        {
            _left.Add(Environment.TickCount64, _done, Whole);
            _show(_left.Text);
        };
    }

    public void Update(double fraction, PatchPhase phase)
    {
        bool newPhase = phase != _phase;
        _phase = phase;
        _held = newPhase ? fraction : Math.Max(_held, fraction);
        long done = (long)(Math.Clamp(fraction, 0, 1) * Whole);
        _done = Math.Clamp((long)(_held * Whole), done, Math.Max(done, Whole));
        if (newPhase || !_timer.IsEnabled) _left.Reset();
        _left.Add(Environment.TickCount64, _done, Whole);
        if (!_timer.IsEnabled) _timer.Start();
        _show(_left.Text);
    }

    public void Clear()
    {
        _timer.Stop();
        _left.Clear();
        _show("");
    }
}
