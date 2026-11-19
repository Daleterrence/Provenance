using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Provenance.Wpf.Controls;

public class GlassBanner : ContentControl
{
    private const double Bleed = 24;

    public static readonly DependencyProperty BackdropProperty =
        DependencyProperty.Register(nameof(Backdrop), typeof(FrameworkElement), typeof(GlassBanner), new PropertyMetadata(null, (d, _) => ((GlassBanner)d).Follow()));

    private readonly VisualBrush _brush = new() { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };

    static GlassBanner() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(GlassBanner), new FrameworkPropertyMetadata(typeof(GlassBanner)));

    public GlassBanner()
    {
        IsTabStop = false;
        SizeChanged += (_, _) => Follow();
        LayoutUpdated += (_, _) => Follow();
    }

    public FrameworkElement? Backdrop { get => (FrameworkElement?)GetValue(BackdropProperty); set => SetValue(BackdropProperty, value); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("Frost") is Shape frost) frost.Fill = _brush;
    }

    private void Follow()
    {
        if (Backdrop is not { } under || !IsVisible || ActualWidth <= 0 || !under.IsVisible) return;
        // a brush's viewbox is in the space the visual's parent sees it in, so its own offset counts
        if (VisualTreeHelper.GetParent(under) is not Visual space) return;
        var at = TransformToVisual(space).Transform(new Point(0, 0));
        var box = new Rect(at.X - Bleed, at.Y - Bleed, ActualWidth + 2 * Bleed, ActualHeight + 2 * Bleed);
        if (_brush.Visual != under) _brush.Visual = under;
        if (_brush.Viewbox != box) _brush.Viewbox = box;
    }
}
