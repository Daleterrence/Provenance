using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Provenance.Wpf.Controls;

public class AppWindow : Window
{
    public static readonly DependencyProperty CardMarginProperty = DependencyProperty.Register(
        nameof(CardMargin), typeof(Thickness), typeof(AppWindow), new PropertyMetadata(new Thickness(14)));

    public static readonly DependencyProperty WideShadowProperty = DependencyProperty.Register(
        nameof(WideShadow), typeof(bool), typeof(AppWindow), new PropertyMetadata(false));

    public Thickness CardMargin { get => (Thickness)GetValue(CardMarginProperty); set => SetValue(CardMarginProperty, value); }

    public bool WideShadow { get => (bool)GetValue(WideShadowProperty); set => SetValue(WideShadowProperty, value); }

    public bool CloseOnEscape { get; set; } = true;

    protected bool OwnerClosing { get; private set; }

    internal static Func<double> WorkAreaHeight { get; set; } = () => SystemParameters.WorkArea.Height;

    public AppWindow()
    {
        Loaded += (_, _) => { if (Owner != null) Owner.Closing += OnOwnerClosing; };
        Closed += (_, _) => { if (Owner != null) Owner.Closing -= OnOwnerClosing; };
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        double room = WorkAreaHeight();
        if (MinHeight > room) MinHeight = room;
        if (MaxHeight > room) MaxHeight = room;
        if (Height > room) Height = room;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (!WindowFrame.Rounded && GetTemplateChild("Card") is Border card) card.CornerRadius = new CornerRadius(0);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowFrame.Apply(this);
    }

    // KeyDown, not PreviewKeyDown, so an open dropdown or menu gets its Esc first
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && CloseOnEscape && OnEscape()) e.Handled = true;
    }

    protected virtual bool OnEscape()
    {
        Close();
        return true;
    }

    public bool? ShowDialogOver(Window? owner)
    {
        if (owner is { IsVisible: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return ShowDialog();
    }

    private void OnOwnerClosing(object? sender, CancelEventArgs e) => OwnerClosing = true;
}
