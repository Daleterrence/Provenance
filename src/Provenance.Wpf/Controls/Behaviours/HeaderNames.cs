using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Provenance.Wpf.Controls.Behaviours;

// a settings row's header is only a label drawn beside its control, so a screen reader gets the control with no name.
// This gives it the header's, unless it has one of its own
public static class HeaderNames
{
    public static readonly DependencyProperty NamesContentProperty = DependencyProperty.RegisterAttached(
        "NamesContent", typeof(bool), typeof(HeaderNames), new PropertyMetadata(false, OnChanged));

    public static bool GetNamesContent(DependencyObject d) => (bool)d.GetValue(NamesContentProperty);
    public static void SetNamesContent(DependencyObject d, bool value) => d.SetValue(NamesContentProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not HeaderedContentControl row) return;
        row.Loaded -= OnLoaded;
        if ((bool)e.NewValue) row.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Name(sender);

    internal static void Name(object sender)
    {
        if (sender is not HeaderedContentControl { Header: string { Length: > 0 } header, Content: DependencyObject content }) return;
        if (FirstControl(content) is { } target && string.IsNullOrEmpty(AutomationProperties.GetName(target))
            && AutomationProperties.GetLabeledBy(target) is null)
            AutomationProperties.SetName(target, header);
    }

    // the content itself when it's a control, otherwise the first one inside it (a slider beside its value). A check box
    // or button already reads out what's written on it
    private static DependencyObject? FirstControl(DependencyObject d)
    {
        if (d is ContentControl { Content: string { Length: > 0 } }) return null;
        if (d is Selector or RangeBase or TextBoxBase or PasswordBox or ToggleButton) return d;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FirstControl(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }
}
