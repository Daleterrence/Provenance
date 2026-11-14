using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Provenance.Wpf.Controls.Behaviours;

namespace Provenance.Wpf.Views.Main;

public partial class PopCard : UserControl
{
    public static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        nameof(Shown), typeof(bool), typeof(PopCard), new PropertyMetadata(false, (d, e) => ((PopCard)d).Pop((bool)e.NewValue)));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(PopCard), new PropertyMetadata("", (d, e) => ((PopCard)d).Say((string)e.NewValue)));
    public static readonly DependencyProperty IsErrorProperty = DependencyProperty.Register(
        nameof(IsError), typeof(bool), typeof(PopCard), new PropertyMetadata(false));

    private const double Rise = 10;
    private static readonly TimeSpan Time = TimeSpan.FromMilliseconds(200);
    private bool _telling;

    public PopCard() => InitializeComponent();

    public bool Shown { get => (bool)GetValue(ShownProperty); set => SetValue(ShownProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsError { get => (bool)GetValue(IsErrorProperty); set => SetValue(IsErrorProperty, value); }

    // the words stay put while the card fades out
    private void Say(string text)
    {
        if (text.Length == 0) return;
        Label.Text = text;
        Tell();
    }

    // once per change, after the layout, with the keyboard focus left where it is
    private void Tell()
    {
        if (_telling) return;
        _telling = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            _telling = false;
            if (!IsVisible || !Shown || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
            UIElementAutomationPeer.CreatePeerForElement(Label)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            Announce.Told?.Invoke(Label);
        });
    }

    private void Pop(bool shown)
    {
        if (!SystemParameters.ClientAreaAnimation)   // Windows "show animations" is off
        {
            Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (shown) Tell();
            return;
        }
        var move = (TranslateTransform)Card.RenderTransform;
        bool fresh = Visibility != Visibility.Visible;
        var ease = new CubicEase { EasingMode = shown ? EasingMode.EaseOut : EasingMode.EaseIn };
        var fade = new DoubleAnimation(shown ? 1 : 0, Time) { EasingFunction = ease };
        var rise = new DoubleAnimation(shown ? 0 : Rise, Time) { EasingFunction = ease };
        if (shown)
        {
            if (fresh) { fade.From = 0; rise.From = Rise; }
            Visibility = Visibility.Visible;
            Tell();
        }
        else
        {
            if (fresh) return;
            fade.Completed += (_, _) => { if (!Shown) Visibility = Visibility.Collapsed; };
        }
        Card.BeginAnimation(OpacityProperty, fade);
        move.BeginAnimation(TranslateTransform.YProperty, rise);
    }
}
