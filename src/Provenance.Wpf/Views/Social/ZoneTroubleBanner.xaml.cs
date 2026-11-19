using System.Windows;
using System.Windows.Controls;
using Provenance.Wpf.Controls.Behaviours;

namespace Provenance.Wpf.Views.Social;

public partial class ZoneTroubleBanner : UserControl
{
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
        nameof(Active), typeof(bool), typeof(ZoneTroubleBanner), new PropertyMetadata(false, OnActiveChanged));

    private static readonly List<WeakReference<ZoneTroubleBanner>> All = [];
    private static bool _announced;

    public ZoneTroubleBanner()
    {
        InitializeComponent();
        lock (All) All.Add(new WeakReference<ZoneTroubleBanner>(this));
    }

    public bool Active
    {
        get => (bool)GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var banner = (ZoneTroubleBanner)d;
        bool on = e.NewValue is true;
        banner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        lock (All)
        {
            All.RemoveAll(r => !r.TryGetTarget(out _));
            if (!on)
            {
                if (!All.Any(r => r.TryGetTarget(out var b) && b.Active)) _announced = false;
                return;
            }
            if (_announced || !banner.IsVisible) return;
            _announced = true;
        }
        Announce.Raise(banner);
    }
}
