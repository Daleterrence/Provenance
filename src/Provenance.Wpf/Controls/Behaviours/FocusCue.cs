using System.Windows;
using System.Windows.Input;

namespace Provenance.Wpf.Controls.Behaviours;

// Shown is true while the focus came from the keyboard. A clicked button keeps keyboard focus too, so going off
// IsKeyboardFocused would leave it lit up. Every element is tracked, so any focus ring can follow it
public static class FocusCue
{
    public static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached(
        "Shown", typeof(bool), typeof(FocusCue), new PropertyMetadata(false));

    // set by the app's focus ring styles, so FocusLook can tell them from a dotted one
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

    // only focus the keyboard moved within the window. Coming back to it (Alt+Tab ends on a key too) restores focus,
    // which mustn't light up the last clicked button
    internal static bool ShowFor(bool lastInputWasKeyboard, DependencyObject? oldFocus, DependencyObject newFocus) =>
        lastInputWasKeyboard && oldFocus is not null && Window.GetWindow(oldFocus) is { } from && from == Window.GetWindow(newFocus);

    private static void OnLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is UIElement element && ReferenceEquals(e.OldFocus, element)) SetShown(element, false);
    }
}
