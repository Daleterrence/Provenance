using System.Windows;
using System.Windows.Input;

namespace Provenance.Wpf.Controls.Behaviours;

// a clicked button keeps keyboard focus too, so IsKeyboardFocused would leave it lit up
public static class FocusCue
{
    public static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached(
        "Shown", typeof(bool), typeof(FocusCue), new PropertyMetadata(false));

    public static readonly DependencyProperty RingProperty = DependencyProperty.RegisterAttached(
        "Ring", typeof(bool), typeof(FocusCue), new PropertyMetadata(false));

    public static bool GetRing(DependencyObject d) => (bool)d.GetValue(RingProperty);
    public static void SetRing(DependencyObject d, bool value) => d.SetValue(RingProperty, value);

    public static bool IsRing(Style? style)
    {
        for (; style is not null; style = style.BasedOn)
            if (style.Setters.OfType<Setter>().Any(s => s.Property == RingProperty)) return true;
        return false;
    }

    static FocusCue()
    {
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnGot), true);
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLost), true);
    }

    public static bool GetShown(DependencyObject d) => (bool)d.GetValue(ShownProperty);
    public static void SetShown(DependencyObject d, bool value) => d.SetValue(ShownProperty, value);

    private static void OnGot(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is UIElement element && ReferenceEquals(e.NewFocus, element))
            SetShown(element, ShowFor(InputManager.Current.MostRecentInputDevice is KeyboardDevice, e.OldFocus as DependencyObject, element));
    }

    internal static bool ShowFor(bool lastInputWasKeyboard, DependencyObject? oldFocus, DependencyObject newFocus) =>
        lastInputWasKeyboard && oldFocus is not null && Window.GetWindow(oldFocus) is { } from && from == Window.GetWindow(newFocus);

    private static void OnLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is UIElement element && ReferenceEquals(e.OldFocus, element)) SetShown(element, false);
    }
}
