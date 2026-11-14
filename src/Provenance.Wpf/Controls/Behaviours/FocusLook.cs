using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Shapes;

namespace Provenance.Wpf.Controls.Behaviours;

// only styles carrying FocusCue.Ring get through, anything else (MaterialDesign's included) becomes the box ring.
// null stays null for controls that draw their own
public static class FocusLook
{
    private static Style? _row, _box, _pill, _round;

    // true when the focus came from the keyboard and the control is one Tab stops on, so a list or scroll viewer that took
    // a click never gets one
    private sealed class RingShown : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values is [bool shown, bool tabStop] ? (shown && tabStop ? Visibility.Visible : Visibility.Collapsed) : Visibility.Visible;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public static Style RowRing => _row ??= Build(new Thickness(0), 6);
    public static Style BoxRing => _box ??= Build(new Thickness(-1), 4);
    public static Style PillRing => _pill ??= Build(new Thickness(-2, -2, 6, 4), 18);
    public static Style RoundRing => _round ??= Build(new Thickness(-3), 41);

    private static Style Build(Thickness margin, double radius)
    {
        var rect = new FrameworkElementFactory(typeof(Rectangle));
        rect.SetValue(FrameworkElement.MarginProperty, margin);
        rect.SetValue(Rectangle.RadiusXProperty, radius);
        rect.SetValue(Rectangle.RadiusYProperty, radius);
        rect.SetValue(Shape.StrokeThicknessProperty, 2.0);
        rect.SetResourceReference(Shape.StrokeProperty, "Primary");
        var adorner = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Adorner), 1);
        rect.SetBinding(UIElement.VisibilityProperty, new MultiBinding
        {
            Converter = new RingShown(),
            FallbackValue = Visibility.Visible,
            Bindings =
            {
                new Binding { Path = new PropertyPath("AdornedElement.(0)", FocusCue.ShownProperty), RelativeSource = adorner },
                new Binding { Path = new PropertyPath("AdornedElement.IsTabStop"), RelativeSource = adorner, FallbackValue = true },
            },
        });
        var style = new Style();
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate { VisualTree = rect }));
        style.Setters.Add(new Setter(FocusCue.RingProperty, true));
        style.Seal();
        return style;
    }

    private static bool _installed;

    // FrameworkElement owns the property so it can't be overridden itself, but each type straight under it can.
    // Has to run before the first element's made
    private static IEnumerable<Type> Roots()
    {
        var wpf = typeof(FrameworkElement).Assembly;
        foreach (var assembly in new[] { wpf, typeof(MaterialDesignThemes.Wpf.Card).Assembly, typeof(FocusLook).Assembly })
            foreach (var type in assembly.GetTypes())
                if (type.BaseType == typeof(FrameworkElement) && !type.IsGenericTypeDefinition) yield return type;
    }

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        foreach (var root in Roots())
            FrameworkElement.FocusVisualStyleProperty.OverrideMetadata(root,
                new FrameworkPropertyMetadata(BoxRing, null, (_, value) => value is null || FocusCue.IsRing(value as Style) ? value : BoxRing));
    }
}
