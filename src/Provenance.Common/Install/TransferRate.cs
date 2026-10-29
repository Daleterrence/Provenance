using static System.FormattableString;

namespace Provenance.Common.Install;

// bytes per second over the last window. A sample below the last one (a failed attempt took bytes back) starts over.
public sealed class TransferRate(TimeSpan window)
{
    private readonly Queue<(long Ms, long Done)> _samples = new();
    private (long Ms, long Done) _last;

    public void Reset() => _samples.Clear();

    public void Add(long nowMs, long done)
    {
        if (_samples.Count > 0 && done < _last.Done) _samples.Clear();
        _samples.Enqueue(_last = (nowMs, done));
        while (_samples.Count > 2 && nowMs - _samples.Peek().Ms > window.TotalMilliseconds) _samples.Dequeue();
    }

    public double? BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2) return null;
            var first = _samples.Peek();
            long ms = _last.Ms - first.Ms;
            return ms <= 0 ? null : (_last.Done - first.Done) * 1000.0 / ms;
        }
    }

    // invariant, a German PC would put "6,60 GB" in an English line
    public static string BarText(long done, long total, double? bytesPerSecond)
    {
        if (total <= 0) return "";
        var percent = Math.Floor(100.0 * Math.Clamp(done, 0, total) / total);
        var rate = bytesPerSecond is { } r ? "  " + Format(r) : "";
        return Invariant($"{percent:0}%{rate}  {Size(done)} of {Size(total)}");
    }

    private static string Format(double bps) =>
        bps >= 1_000_000 ? Invariant($"{bps / 1_000_000:0.0} MB/s") : Invariant($"{bps / 1_000:0} KB/s");

    private static string Size(long bytes) => bytes >= 1_000_000_000 ? Invariant($"{bytes / 1e9:0.00} GB") : Invariant($"{bytes / 1e6:0} MB");
}
