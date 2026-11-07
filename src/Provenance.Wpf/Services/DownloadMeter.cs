using System.Windows.Threading;
using Provenance.Common.Progress;

namespace Provenance.Wpf.Services;

// The bar and the text under it, UI thread only. Sampled every second so a stall shows the rate dropping and the time
// left going away. The bar never goes back within a step, a retry that takes bytes back just holds it
public sealed class DownloadMeter
{
    private const long Whole = 1_000_000;

    private readonly TransferRate _rate = new(TimeSpan.FromSeconds(5));
    private readonly TimeLeft _left = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Action<string> _show;
    private readonly Action<string>? _showTimeLeft;
    private readonly Action<double>? _showFraction;
    private long _done, _total;
    private bool _download, _amount;
    private object? _step;
    private double _held;

    public DownloadMeter(Action<string> show, Action<string>? showTimeLeft = null, Action<double>? showFraction = null)
    {
        _show = show;
        _showTimeLeft = showTimeLeft;
        _showFraction = showFraction;
        _timer.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;
            if (_download) _rate.Add(now, _done);
            _left.Add(now, _done, _total);
            Show();
        };
    }

    // fraction is what the bar shows, under 0 leaves the bar alone. A new step starts the bar and the pace over.
    // download false is local work (checking, unpacking), which shows no rate. amount false leaves the bar text to
    // whoever already shows the sizes and just keeps the time left
    public void Update(long done, long total, double fraction, bool download = true, bool amount = true, object? step = null)
    {
        long now = Environment.TickCount64;
        bool newStep = NewStep(step);
        if (fraction >= 0) done = Hold(fraction, newStep, done, total);
        if (total != _total || download != _download || newStep)
        {
            _rate.Reset();
            _rate.Add(now, done);
            _left.Reset();
        }
        _done = done;
        _total = total;
        _download = download;
        _amount = amount;
        _left.Add(now, done, total);
        if (!_timer.IsEnabled) _timer.Start();
        Show();
    }

    // time left from the fraction alone, no sizes
    public void Update(double fraction, object? step = null) =>
        Update((long)(Math.Clamp(fraction, 0, 1) * Whole), Whole, fraction, download: false, amount: false, step);

    // for a stage that has a fraction but no bytes
    public void Bar(double fraction, object? step = null)
    {
        bool newStep = NewStep(step);
        if (fraction >= 0) Hold(fraction, newStep, 0, 0);
    }

    private bool NewStep(object? step)
    {
        bool changed = !Equals(step, _step);
        _step = step;
        return changed;
    }

    public void Clear()
    {
        _timer.Stop();
        _rate.Reset();
        _left.Reset();
        _total = 0;
        _show("");
        _showTimeLeft?.Invoke("");
    }

    // Clear, and the bar starts again from the left
    public void Restart()
    {
        Clear();
        _step = null;
        _held = 0;
        _showFraction?.Invoke(0);
    }

    // the bytes the held bar stands for, so the text under it doesn't drop back on a retry either
    private long Hold(double fraction, bool newStep, long done, long total)
    {
        _held = newStep ? fraction : Math.Max(_held, fraction);
        _showFraction?.Invoke(_held);
        return total <= 0 ? done : Math.Clamp((long)(_held * total), done, Math.Max(done, total));
    }

    private void Show()
    {
        _show(_amount ? TransferRate.BarText(_done, _total, _download ? _rate.BytesPerSecond : null) : "");
        _showTimeLeft?.Invoke(_left.Text);
    }
}
