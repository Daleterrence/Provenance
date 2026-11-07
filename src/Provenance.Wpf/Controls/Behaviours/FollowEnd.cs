using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Provenance.Wpf.Controls.Behaviours;

// A chat log's scroll viewer follows new lines, but only if the reader was already at the end, so scrolling back in a
// busy room doesn't get undone. Pin picks the changes that always come into view (a line you just sent, say)
public static class FollowEnd
{
    // how close to the end still counts as being there
    private const double Slack = 24;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(INotifyCollectionChanged), typeof(FollowEnd), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty PinProperty = DependencyProperty.RegisterAttached(
        "Pin", typeof(Func<NotifyCollectionChangedEventArgs, bool>), typeof(FollowEnd), new PropertyMetadata(null));

    private static readonly ConditionalWeakTable<ScrollViewer, Follower> Followers = new();

    public static INotifyCollectionChanged? GetSource(DependencyObject d) => (INotifyCollectionChanged?)d.GetValue(SourceProperty);
    public static void SetSource(DependencyObject d, INotifyCollectionChanged? value) => d.SetValue(SourceProperty, value);
    public static Func<NotifyCollectionChangedEventArgs, bool>? GetPin(DependencyObject d) =>
        (Func<NotifyCollectionChangedEventArgs, bool>?)d.GetValue(PinProperty);
    public static void SetPin(DependencyObject d, Func<NotifyCollectionChangedEventArgs, bool>? value) => d.SetValue(PinProperty, value);

    public static bool AtEnd(ScrollViewer scroll) => scroll.VerticalOffset >= scroll.ScrollableHeight - Slack;

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer scroll) Followers.GetValue(scroll, s => new Follower(s)).Follow(e.NewValue as INotifyCollectionChanged);
    }

    // the view model outlives the window, so a closed one lets go or it keeps scrolling in the background
    private sealed class Follower
    {
        private readonly ScrollViewer _scroll;
        private INotifyCollectionChanged? _source;
        private bool _listening;

        public Follower(ScrollViewer scroll)
        {
            _scroll = scroll;
            scroll.Loaded += (_, _) => Listen(true);
            scroll.Unloaded += (_, _) => Listen(false);
        }

        public void Follow(INotifyCollectionChanged? source)
        {
            Listen(false);
            _source = source;
            Listen(true);
        }

        private void Listen(bool on)
        {
            if (_source is null || on == _listening) return;
            _listening = on;
            if (on) _source.CollectionChanged += OnChanged;
            else _source.CollectionChanged -= OnChanged;
        }

        private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)) return;
            // the pin decides first, it keeps track of what's on show
            bool pinned = GetPin(_scroll)?.Invoke(e) == true;
            if (pinned || AtEnd(_scroll))
                _scroll.Dispatcher.InvokeAsync(_scroll.ScrollToEnd, DispatcherPriority.Background);
        }
    }
}
