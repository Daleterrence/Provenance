using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Provenance.Wpf.Controls.Behaviours;

public static class KeyScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(KeyScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBoxBase box) return;
        box.PreviewKeyDown -= OnKey;
        if ((bool)e.NewValue) box.PreviewKeyDown += OnKey;
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var box = (TextBoxBase)sender;
        switch (e.Key)
        {
            case Key.Down: box.LineDown(); break;
            case Key.Up: box.LineUp(); break;
            case Key.PageDown: box.PageDown(); break;
            case Key.PageUp: box.PageUp(); break;
            default: return;
        }
        e.Handled = true;
    }
}
