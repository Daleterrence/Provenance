using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Provenance.Wpf.Controls.Behaviours;

// For a tooltip that's the whole of a line that can be cut short: it only opens while the line is cut
public static class TrimTip
{
    public static readonly DependencyProperty OnlyWhenCutProperty = DependencyProperty.RegisterAttached(
        "OnlyWhenCut", typeof(bool), typeof(TrimTip), new PropertyMetadata(false, OnChanged));

    public static bool GetOnlyWhenCut(DependencyObject d) => (bool)d.GetValue(OnlyWhenCutProperty);
    public static void SetOnlyWhenCut(DependencyObject d, bool value) => d.SetValue(OnlyWhenCutProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.ToolTipOpening -= OnOpening;
        if (e.NewValue is true) element.ToolTipOpening += OnOpening;
    }

    private static void OnOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is FrameworkElement element && !IsCut(element)) e.Handled = true;
    }

    // a panel's cut when any line straight inside it is
    internal static bool IsCut(FrameworkElement element) => element switch
    {
        TextBlock text => IsCut(text),
        FitText fit => fit.IsCut,
        Panel panel => panel.Children.OfType<FrameworkElement>().Any(c => c is TextBlock or FitText && IsCut(c)),
        _ => false,
    };

    // WPF won't say whether it trimmed, so the text's measured again: one line against the width, a wrapped one against the height
    private static bool IsCut(TextBlock block)
    {
        // Text is empty for a block made of runs
        var said = block.Text.Length > 0 ? block.Text : new TextRange(block.ContentStart, block.ContentEnd).Text;
        if (said.Length == 0 || block.ActualWidth <= 0) return false;
        var text = new FormattedText(said, CultureInfo.CurrentUICulture, block.FlowDirection,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.Black, null,
            TextOptions.GetTextFormattingMode(block), VisualTreeHelper.GetDpi(block).PixelsPerDip);
        double width = Math.Max(1, block.ActualWidth - block.Padding.Left - block.Padding.Right);
        if (block.TextWrapping == TextWrapping.NoWrap) return text.Width > width + 0.5;
        text.MaxTextWidth = width;
        return text.Height > block.ActualHeight - block.Padding.Top - block.Padding.Bottom + 0.5;
    }
}
