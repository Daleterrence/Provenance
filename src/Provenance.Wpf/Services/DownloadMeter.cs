using System.Windows.Threading;
using Provenance.Common.Progress;

namespace Provenance.Wpf.Services;

// The text under a progress bar, UI thread only. Sampled every second so a stall shows the rate dropping and the time
// left going away
public sealed class DownloadMeter
{
    private readonly TransferRate _rate = new(TimeSpan.FromSeconds(5));
    private readonly TimeLeft _left = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Action<string> _show;
    private readonly Action<string>? _showTimeLeft;
    private long _done, _total;
    private bool _download, _amount;

    public DownloadMeter(Action<string> show, Action<string>? showTimeLeft = null)
    {
        _show = show;
        _showTimeLeft = showTimeLeft;
        _timer.Tick += (_, _) =>
        {
            long now = Environment.TickCount64;
            if (_download) _rate.Add(now, _done);
            _left.Add(now, _done, _total);
            Show();
        };
    }

    // download false is local work (checking, unpacking), which shows no rate. amount false leaves the bar text to
    // whoever already shows the sizes and just keeps the time left
    public void Update(long done, long total, bool download = true, bool amount = true)
    {
        long now = Environment.TickCount64;
        if (total != _total || download != _download)
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

    public void Clear()
    {
        _timer.Stop();
        _rate.Reset();
        _left.Reset();
        _total = 0;
        _show("");
        _showTimeLeft?.Invoke("");
    }

    private void Show()
    {
        _show(_amount ? TransferRate.BarText(_done, _total, _download ? _rate.BytesPerSecond : null) : "");
        _showTimeLeft?.Invoke(_left.Text);
    }
}
