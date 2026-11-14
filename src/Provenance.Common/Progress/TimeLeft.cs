namespace Provenance.Common.Progress;

// "About 3 minutes left" under a progress bar. Says it's estimating until it's seen 10s of steady progress, then a
// smoothed countdown that stays up and keeps counting until Clear, even when the pace goes strange
public sealed class TimeLeft
{
    public const string Estimating = "Estimating remaining time...";

    private const long SettleMs = 10_000, WindowMs = 30_000, StallMs = 10_000, SampleMs = 250;
    private const double SmoothMs = 10_000;

    private readonly Queue<(long Ms, double Done)> _samples = new();
    private double _total, _done = double.NaN;
    private long _startMs, _lastMs, _sampleMs, _movedMs, _nowMs, _shownMs;
    private double? _left, _shown;
    private bool _stalled, _active;

    // starts the estimate over, what's already on show carries on counting down meanwhile
    public void Reset()
    {
        _samples.Clear();
        _done = double.NaN;
        _left = null;
    }

    // the next job starts from estimating again
    public void Clear()
    {
        Reset();
        _shown = null;
        _active = false;
    }

    // done and total in any unit, bytes or a fraction. A new total or progress going backwards starts it over
    public void Add(long nowMs, double done, double total)
    {
        _active = true;
        _nowMs = nowMs;
        if (!double.IsFinite(done) || !double.IsFinite(total) || total <= 0) { Reset(); return; }
        // a stall or a sleep says nothing about the pace once it moves again, so it settles from scratch
        if (_samples.Count == 0 || total != _total || done < _done || nowMs - _sampleMs > WindowMs || (_stalled && done > _done))
        {
            Start(nowMs, done, total);
            return;
        }
        if (done == _done && nowMs - _movedMs >= StallMs)
        {
            Start(nowMs, done, total);
            _stalled = true;
            return;
        }
        if (done > _done) _movedMs = nowMs;
        _done = done;
        if (nowMs - _sampleMs < SampleMs) return;
        _samples.Enqueue((_sampleMs = nowMs, done));
        while (_samples.Count > 2 && nowMs - _samples.Peek().Ms > WindowMs) _samples.Dequeue();

        var first = _samples.Peek();
        long span = nowMs - first.Ms;
        if (span < 1000) return;
        double rate = (done - first.Done) / span;
        if (rate <= 0) return;
        double raw = (total - done) / rate;
        if (_left is not { } left || nowMs - _startMs < SettleMs) _left = raw;
        else
        {
            long dt = nowMs - _lastMs;
            double counted = Math.Max(0, left - dt);
            _left = counted + (1 - Math.Exp(-dt / SmoothMs)) * (raw - counted);
        }
        _lastMs = nowMs;
        if (nowMs - _startMs >= SettleMs && double.IsFinite(_left.Value)) { _shown = _left; _shownMs = nowMs; }
    }

    // blank only before a job's first reading
    public string Text =>
        _shown is { } shown ? Describe(Math.Max(0, shown - (_nowMs - _shownMs))) : _active ? Estimating : "";

    private void Start(long nowMs, double done, double total)
    {
        Reset();
        _stalled = false;
        _total = total;
        _startMs = _lastMs = _sampleMs = _movedMs = nowMs;
        _samples.Enqueue((nowMs, _done = done));
    }

    internal static string Describe(double ms)
    {
        double minutes = ms / 60_000;
        if (minutes < 1) return "Less than a minute left";
        int m = (int)Math.Round(minutes, MidpointRounding.AwayFromZero);
        if (m < 60) return m == 1 ? "About 1 minute left" : $"About {m} minutes left";
        if (m < 180)
        {
            int h = m / 60, rest = (int)Math.Round(m % 60 / 5.0, MidpointRounding.AwayFromZero) * 5;
            if (rest == 60) { h++; rest = 0; }
            var hours = h == 1 ? "1 hour" : $"{h} hours";
            return rest == 0 ? $"About {hours} left" : $"About {hours} and {rest} minutes left";
        }
        return $"About {(int)Math.Round(minutes / 60, MidpointRounding.AwayFromZero)} hours left";
    }
}
