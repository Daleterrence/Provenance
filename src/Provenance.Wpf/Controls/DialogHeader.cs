using System.Windows;
using System.Windows.Controls;

namespace Provenance.Wpf.Controls;

public class DialogHeader : Control
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(DialogHeader), new PropertyMetadata(null));

    static DialogHeader() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(DialogHeader), new FrameworkPropertyMetadata(typeof(DialogHeader)));

    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Close") is Button close)
            close.Click += (_, _) => { if (Window.GetWindow(this) is { } window) SystemCommands.CloseWindow(window); };
    }
}
