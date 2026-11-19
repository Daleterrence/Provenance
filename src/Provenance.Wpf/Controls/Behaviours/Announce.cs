using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;

namespace Provenance.Wpf.Controls.Behaviours;

public static class Announce
{
    public static readonly DependencyProperty WhenShownProperty = DependencyProperty.RegisterAttached(
        "WhenShown", typeof(bool), typeof(Announce), new PropertyMetadata(false, OnChanged));

    public static bool GetWhenShown(DependencyObject d) => (bool)d.GetValue(WhenShownProperty);
    public static void SetWhenShown(DependencyObject d, bool value) => d.SetValue(WhenShownProperty, value);

    public static readonly DependencyProperty OnceProperty = DependencyProperty.RegisterAttached(
        "Once", typeof(bool), typeof(Announce), new PropertyMetadata(false, OnOnceChanged));

    public static bool GetOnce(DependencyObject d) => (bool)d.GetValue(OnceProperty);
    public static void SetOnce(DependencyObject d, bool value) => d.SetValue(OnceProperty, value);

    internal static Action<UIElement>? Told;

    private static void OnOnceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if (e.NewValue is not true)
        {
            element.ClearValue(AutomationProperties.LiveSettingProperty);
            return;
        }
        AutomationProperties.SetLiveSetting(element, AutomationLiveSetting.Polite);
        if (element.IsVisible)
        {
            Raise(element);
            return;
        }
        DependencyPropertyChangedEventHandler? first = null;
        first = (_, args) =>
        {
            if (args.NewValue is not true) return;
            element.IsVisibleChanged -= first;
            Raise(element);
        };
        element.IsVisibleChanged += first;
    }

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
        element.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (!element.IsVisible || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
            UIElementAutomationPeer.CreatePeerForElement(element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            Told?.Invoke(element);
        });
    }
}
