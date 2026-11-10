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

    // the see-through margin round a dialog card, which is where its shadow falls
    public Thickness CardMargin { get => (Thickness)GetValue(CardMarginProperty); set => SetValue(CardMarginProperty, value); }

    public bool WideShadow { get => (bool)GetValue(WideShadowProperty); set => SetValue(WideShadowProperty, value); }

    // Esc closes unless a window turns it off (the dialogs have a cancel button for it) or answers it itself
    public bool CloseOnEscape { get; set; } = true;

    // the app's closing (tray Exit), so a question here would only leave a window up with the tray gone
    protected bool OwnerClosing { get; private set; }

    public AppWindow()
    {
        Loaded += (_, _) => { if (Owner != null) Owner.Closing += OnOwnerClosing; };
        Closed += (_, _) => { if (Owner != null) Owner.Closing -= OnOwnerClosing; };
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

    // KeyDown, not PreviewKeyDown, so an open dropdown or menu gets its Esc first and only closes itself
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
