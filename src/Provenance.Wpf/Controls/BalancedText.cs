using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Provenance.Wpf.Controls;

public sealed class BalancedText : FrameworkElement
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(BalancedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => ((BalancedText)d)._text.Text = NoBreak.Glue((string?)e.NewValue ?? "")));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(BalancedText));
    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(BalancedText));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(BalancedText));

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };

    static BalancedText() =>
        HorizontalAlignmentProperty.OverrideMetadata(typeof(BalancedText), new FrameworkPropertyMetadata(HorizontalAlignment.Center));

    public BalancedText() => AddVisualChild(_text);

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
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
        _text.Measure(available);
        var full = _text.DesiredSize;
        if (double.IsInfinity(available.Width) || full.Width <= 0) return full;
        double low = 0, high = Math.Min(available.Width, full.Width + 1);
        for (int i = 0; i < 10; i++)
        {
            double mid = (low + high) / 2;
            _text.Measure(new Size(mid, available.Height));
            if (_text.DesiredSize.Height <= full.Height + 0.5) high = mid;
            else low = mid;
        }
        _text.Measure(new Size(high, available.Height));
        return new Size(Math.Min(available.Width, high + 1), _text.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        _text.Arrange(new Rect(final));
        return final;
    }
}
