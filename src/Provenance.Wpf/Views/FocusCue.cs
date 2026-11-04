using System.Windows;
using System.Windows.Input;

namespace Provenance.Wpf.Views;

// Shown is true while the focus came from the keyboard. A clicked button keeps keyboard focus too, so going off
// IsKeyboardFocused would leave it lit up
public static class FocusCue
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(bool), typeof(FocusCue), new PropertyMetadata(false, OnTrackChanged));

    public static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached(
        "Shown", typeof(bool), typeof(FocusCue), new PropertyMetadata(false));

    public static bool GetTrack(DependencyObject d) => (bool)d.GetValue(TrackProperty);
    public static void SetTrack(DependencyObject d, bool value) => d.SetValue(TrackProperty, value);
    public static bool GetShown(DependencyObject d) => (bool)d.GetValue(ShownProperty);
    public static void SetShown(DependencyObject d, bool value) => d.SetValue(ShownProperty, value);

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.GotKeyboardFocus -= OnGot;
        element.LostKeyboardFocus -= OnLost;
        if (e.NewValue is true)
        {
            element.GotKeyboardFocus += OnGot;
            element.LostKeyboardFocus += OnLost;
        }
    }

    private static void OnGot(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is UIElement element && ReferenceEquals(e.NewFocus, element))
            SetShown(element, ShowFor(InputManager.Current.MostRecentInputDevice is KeyboardDevice, e.OldFocus as DependencyObject, element));
    }

    // only focus the keyboard moved within a window. Coming back to the window (Alt+Tab ends on a key too) puts focus
    // back where it was, and that mustn't light up the button that was last clicked
    internal static bool ShowFor(bool lastInputWasKeyboard, DependencyObject? oldFocus, DependencyObject newFocus) =>
        lastInputWasKeyboard && oldFocus is not null && Window.GetWindow(oldFocus) is { } from && from == Window.GetWindow(newFocus);

    private static void OnLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is UIElement element && ReferenceEquals(e.OldFocus, element)) SetShown(element, false);
    }
}
