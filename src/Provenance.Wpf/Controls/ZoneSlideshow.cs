using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;

namespace Provenance.Wpf.Controls;

// the installer's zone pictures, cross-faded in a random order with their edges faded out
public sealed class ZoneSlideshow : Grid
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(8), Fade = TimeSpan.FromSeconds(1.5);
    private readonly DispatcherTimer _timer = new() { Interval = Hold };
    private readonly ZoneOrder _order;
    private Image _front = Picture(), _back = Picture();

    public ZoneSlideshow() : this(Path.Combine(AppContext.BaseDirectory, "zones")) { }

    internal ZoneSlideshow(string folder)
    {
        _order = new ZoneOrder(ZoneOrder.Find(folder), Random.Shared);
        ClipToBounds = true;
        // one gradient only fades one direction, so the sides and the top and bottom get a mask each
        OpacityMask = Edges(horizontal: true, 0.1);
        var inner = new Grid { OpacityMask = Edges(horizontal: false, 0.22) };
        inner.Children.Add(_back);
        inner.Children.Add(_front);
        Children.Add(inner);
        _timer.Tick += (_, _) => Next();
        IsVisibleChanged += (_, _) => { if (IsVisible) Start(); else _timer.Stop(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    internal int Pictures => _order.Count;

    private void Start()
    {
        if (_order.Count == 0) return;
        if (_front.Source is null && Load(_order.Next()) is { } first)
        {
            _front.Source = first;
            _front.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, SystemParameters.ClientAreaAnimation ? Fade : TimeSpan.Zero));
        }
        _timer.Start();
    }

    private void Next()
    {
        if (Load(_order.Next()) is not { } next) return;
        _back.Source = next;
        var fade = SystemParameters.ClientAreaAnimation ? Fade : TimeSpan.Zero;
        _back.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, fade));
        _front.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, fade));
        (_front, _back) = (_back, _front);
    }

    private static BitmapImage? Load(string? path)
    {
        if (path is null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Could not load the zone picture {Path}", path);
            return null;
        }
    }

    private static Image Picture() => new() { Stretch = Stretch.UniformToFill, Opacity = 0 };

    private static LinearGradientBrush Edges(bool horizontal, double fade)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = horizontal ? new Point(1, 0) : new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        brush.GradientStops.Add(new GradientStop(Colors.Black, fade));
        brush.GradientStops.Add(new GradientStop(Colors.Black, 1 - fade));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        brush.Freeze();
        return brush;
    }
}

// every picture once in a random order, then a new order that never starts on the one just shown
internal sealed class ZoneOrder(IReadOnlyList<string> files, Random random)
{
    private readonly List<string> _round = [];
    private string? _last;

    public int Count => files.Count;

    public static IReadOnlyList<string> Find(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.jpg") : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    public string? Next()
    {
        if (files.Count == 0) return null;
        if (_round.Count == 0)
        {
            _round.AddRange(files.OrderBy(_ => random.Next()));
            if (_round.Count > 1 && _round[^1] == _last) (_round[0], _round[^1]) = (_round[^1], _round[0]);
        }
        _last = _round[^1];
        _round.RemoveAt(_round.Count - 1);
        return _last;
    }
}
