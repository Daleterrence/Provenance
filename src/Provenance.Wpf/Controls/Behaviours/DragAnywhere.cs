using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Provenance.Wpf.Controls.Behaviours;

public static class DragAnywhere
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(DragAnywhere), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window) return;
        window.MouseLeftButtonDown -= OnDown;
        if (e.NewValue is true) window.MouseLeftButtonDown += OnDown;
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Window window && e.ButtonState == MouseButtonState.Pressed && CanStartFrom(e.OriginalSource))
            window.DragMove();
    }

    // a text box's own clicks land on parts inside it, so it's the whole chain up from where the click landed
    internal static bool CanStartFrom(object? source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TextBoxBase or PasswordBox) return false;
        return true;
    }
}
