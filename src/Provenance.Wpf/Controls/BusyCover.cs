#pragma warning disable RS0030
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Provenance.Wpf.Controls;

internal static class BusyCover
{
    private sealed class Held(CardAdorner adorner, UIElement root, bool wasEnabled)
    {
        public CardAdorner Adorner { get; } = adorner;
        public UIElement Root { get; } = root;
        public bool WasEnabled { get; } = wasEnabled;
    }

    private sealed class CardAdorner : Adorner
    {
        private readonly BusyCard _card;

        public CardAdorner(UIElement adorned, string text) : base(adorned)
        {
            _card = new BusyCard { Text = text };
            AddVisualChild(_card);
        }

        public string Text { set => _card.Text = value; }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _card;

        protected override Size MeasureOverride(Size constraint)
        {
            _card.Measure(constraint);
            return constraint;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _card.Arrange(new Rect(finalSize));
            return finalSize;
        }
    }

    private static readonly ConditionalWeakTable<Window, Held> Covers = new();
    private static Func<Window, bool> _covered = _ => false;
    private static bool _installed;
    private static string? _text;

    // a window that opens while the cover's on gets one too
    public static void Install(Func<Window, bool> covered)
    {
        _covered = covered;
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
    }

    // null takes it off again
    public static void Set(string? text)
    {
        _text = text;
        if (Application.Current is null) return;
        foreach (var w in Application.Current.Windows.OfType<Window>().Where(_covered))
        {
            if (text is null) Remove(w);
            else Put(w, text);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_text is { } text && sender is Window w && _covered(w)) Put(w, text);
    }

    private static void Put(Window w, string text)
    {
        if (Covers.TryGetValue(w, out var held))
        {
            held.Adorner.Text = text;
            return;
        }
        if (w.Content is not UIElement root || AdornerLayer.GetAdornerLayer(root) is not { } layer) return;
        var adorner = new CardAdorner(root, text);
        layer.Add(adorner);
        Covers.Add(w, new Held(adorner, root, root.IsEnabled));
        root.IsEnabled = false;
        w.PreviewKeyDown += Swallow;
    }

    private static void Remove(Window w)
    {
        if (!Covers.TryGetValue(w, out var held)) return;
        Covers.Remove(w);
        w.PreviewKeyDown -= Swallow;
        AdornerLayer.GetAdornerLayer(held.Root)?.Remove(held.Adorner);
        held.Root.IsEnabled = held.WasEnabled;
    }

    // Esc and the rest would still reach the window itself with its content off
    private static void Swallow(object sender, KeyEventArgs e) => e.Handled = true;
}
