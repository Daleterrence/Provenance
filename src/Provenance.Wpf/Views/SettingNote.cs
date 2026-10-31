using System.Windows;

namespace Provenance.Wpf.Views;

public static class SettingNote
{
    public static readonly DependencyProperty SharedProperty =
        DependencyProperty.RegisterAttached("Shared", typeof(bool), typeof(SettingNote), new FrameworkPropertyMetadata(false));

    public static bool GetShared(DependencyObject o) => (bool)o.GetValue(SharedProperty);

    public static void SetShared(DependencyObject o, bool value) => o.SetValue(SharedProperty, value);
}
