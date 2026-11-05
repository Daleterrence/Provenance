using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Provenance.Wpf.Controls;

// Long when it fits the width it's given, Short when it doesn't (trimmed if even that won't fit)
public sealed class FitText : FrameworkElement
{
    public static readonly DependencyProperty LongProperty = DependencyProperty.Register(
        nameof(Long), typeof(string), typeof(FitText), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ShortProperty = DependencyProperty.Register(
        nameof(Short), typeof(string), typeof(FitText), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(FitText));
    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(FitText));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(FitText));

    private readonly TextBlock _text = new() { TextTrimming = TextTrimming.CharacterEllipsis };

    public FitText() => AddVisualChild(_text);

    public string Long
    {
        get => (string)GetValue(LongProperty);
        set => SetValue(LongProperty, value);
    }

    public string Short
    {
        get => (string)GetValue(ShortProperty);
        set => SetValue(ShortProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public string Shown => _text.Text;

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _text : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size available)
    {
        var text = Long ?? "";
        if (!double.IsInfinity(available.Width) && TextWidth(text) > available.Width) text = Short ?? "";
        _text.Text = text;
        _text.Measure(available);
        return _text.DesiredSize;
    }

    protected override Size ArrangeOverride(Size final)
    {
        _text.Arrange(new Rect(final));
        return final;
    }

    private double TextWidth(string text) =>
        text.Length == 0 ? 0 : new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection,
            new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight, _text.FontStretch), _text.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
}
