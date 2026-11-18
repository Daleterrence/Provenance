using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;

namespace Provenance.Wpf.Controls.Behaviours;

// a screen reader reads the element out each time it comes on screen, with the keyboard focus left where it is
public static class Announce
{
    public static readonly DependencyProperty WhenShownProperty = DependencyProperty.RegisterAttached(
        "WhenShown", typeof(bool), typeof(Announce), new PropertyMetadata(false, OnChanged));

    public static bool GetWhenShown(DependencyObject d) => (bool)d.GetValue(WhenShownProperty);
    public static void SetWhenShown(DependencyObject d, bool value) => d.SetValue(WhenShownProperty, value);

    internal static Action<UIElement>? Told;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.IsVisibleChanged -= OnVisibleChanged;
        if (e.NewValue is not true)
        {
            element.ClearValue(AutomationProperties.LiveSettingProperty);
            return;
        }
        AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);
        element.IsVisibleChanged += OnVisibleChanged;
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not UIElement element) return;
        Raise(element);
    }

    public static void Raise(UIElement element)
    {
        // after the layout, a reader can't find an element that's only just been shown
        element.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (!element.IsVisible || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
            UIElementAutomationPeer.CreatePeerForElement(element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            Told?.Invoke(element);
        });
    }
}
