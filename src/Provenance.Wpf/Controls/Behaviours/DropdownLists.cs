using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Provenance.Wpf.Controls.Behaviours;

public static class DropdownLists
{
    private static ComboBoxItem? _entering;

    public static void Install()
    {
        EventManager.RegisterClassHandler(typeof(ComboBoxItem), Mouse.MouseEnterEvent, new MouseEventHandler(OnEnter), true);
        EventManager.RegisterClassHandler(typeof(ComboBoxItem), FrameworkElement.RequestBringIntoViewEvent,
                                          new RequestBringIntoViewEventHandler(OnBringIntoView));
        EventManager.RegisterClassHandler(typeof(ComboBoxItem), Mouse.MouseWheelEvent, new MouseWheelEventHandler(OnWheel));
    }

    // hovering a half-shown row gives it the keyboard, and WPF scrolls whatever has the keyboard into view
    private static void OnEnter(object sender, MouseEventArgs e)
    {
        var row = (ComboBoxItem)sender;
        _entering = row;
        row.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            if (ReferenceEquals(_entering, row)) _entering = null;
        });
    }

    private static void OnBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (ReferenceEquals(sender, _entering)) e.Handled = true;
    }

    // a list that scrolls by the pixel moves 16 a line, under half a row
    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var row = (ComboBoxItem)sender;
        if (VisualTreeHelper.GetParent(row) is not VirtualizingStackPanel) return;
        if (ItemsControl.ItemsControlFromItemContainer(row) is not { } box || VirtualizingPanel.GetScrollUnit(box) != ScrollUnit.Pixel) return;
        DependencyObject? up = row;
        while (up is not null and not ScrollViewer) up = VisualTreeHelper.GetParent(up);
        if (up is not ScrollViewer list) return;
        int lines = SystemParameters.WheelScrollLines;
        double notch = lines < 0 ? list.ViewportHeight : lines * row.ActualHeight;
        list.ScrollToVerticalOffset(list.VerticalOffset - notch * e.Delta / Mouse.MouseWheelDeltaForOneLine);
        e.Handled = true;
    }
}
