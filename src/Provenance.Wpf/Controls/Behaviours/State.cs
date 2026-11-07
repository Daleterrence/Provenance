using System.Windows;

namespace Provenance.Wpf.Controls.Behaviours;

// Flags the shared styles pick their look from, so a view reads State.IsError="{Binding Failed}" and not a bare Tag
public static class State
{
    public static readonly DependencyProperty IsErrorProperty = DependencyProperty.RegisterAttached(
        "IsError", typeof(bool), typeof(State), new PropertyMetadata(false));

    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.RegisterAttached(
        "IsCurrent", typeof(bool), typeof(State), new PropertyMetadata(false));

    // -1 until a view sets it, so a style waiting for zero doesn't take unset for it
    public static readonly DependencyProperty CountProperty = DependencyProperty.RegisterAttached(
        "Count", typeof(int), typeof(State), new PropertyMetadata(-1));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.RegisterAttached(
        "Status", typeof(string), typeof(State), new PropertyMetadata(null));

    public static int GetCount(DependencyObject d) => (int)d.GetValue(CountProperty);
    public static void SetCount(DependencyObject d, int value) => d.SetValue(CountProperty, value);
    public static string? GetStatus(DependencyObject d) => (string?)d.GetValue(StatusProperty);
    public static void SetStatus(DependencyObject d, string? value) => d.SetValue(StatusProperty, value);
    public static bool GetIsError(DependencyObject d) => (bool)d.GetValue(IsErrorProperty);
    public static void SetIsError(DependencyObject d, bool value) => d.SetValue(IsErrorProperty, value);
    public static bool GetIsCurrent(DependencyObject d) => (bool)d.GetValue(IsCurrentProperty);
    public static void SetIsCurrent(DependencyObject d, bool value) => d.SetValue(IsCurrentProperty, value);
}
