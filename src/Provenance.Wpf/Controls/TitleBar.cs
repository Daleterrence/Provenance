using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Provenance.Wpf.Controls;

// Content is whatever sits between the title and the window's own buttons. Everything on the bar gets the light
// colours, the bar's dark in both themes
public class TitleBar : ContentControl
{
    private static readonly ResourceDictionary Light = new() { Source = new Uri("/Provenance;component/Themes/TitleBar.xaml", UriKind.Relative) };

    private static ImageSource? _crystal;

    public static readonly DependencyProperty TitleProperty = Register(nameof(Title), typeof(string), null);
    public static readonly DependencyProperty IconProperty = Register(nameof(Icon), typeof(ImageSource), null);
    public static readonly DependencyProperty TitleSizeProperty = Register(nameof(TitleSize), typeof(double), 14.0);
    public static readonly DependencyProperty DimTitleProperty = Register(nameof(DimTitle), typeof(bool), false);
    public static readonly DependencyProperty GlyphSizeProperty = Register(nameof(GlyphSize), typeof(double), 18.0);
    public static readonly DependencyProperty ShowMaximiseProperty = Register(nameof(ShowMaximise), typeof(bool), false);
    public static readonly DependencyProperty ShowCloseProperty = Register(nameof(ShowClose), typeof(bool), true);
    public static readonly DependencyProperty CloseToolTipProperty = Register(nameof(CloseToolTip), typeof(string), null);
    public static readonly DependencyProperty CloseNameProperty = Register(nameof(CloseName), typeof(string), "Close");

    private static readonly DependencyPropertyKey IsMaximisedKey = DependencyProperty.RegisterReadOnly(
        nameof(IsMaximised), typeof(bool), typeof(TitleBar), new PropertyMetadata(false));
    public static readonly DependencyProperty IsMaximisedProperty = IsMaximisedKey.DependencyProperty;

    static TitleBar() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(TitleBar), new FrameworkPropertyMetadata(typeof(TitleBar)));

    public TitleBar()
    {
        Resources.MergedDictionaries.Add(Light);
        if (_crystal is null)
        {
            var crystal = new BitmapImage(new Uri("pack://application:,,,/Assets/provenance-256.png"));
            crystal.Freeze();
            _crystal = crystal;
        }
        Icon = _crystal;
    }

    private static DependencyProperty Register(string name, Type type, object? value) =>
        DependencyProperty.Register(name, type, typeof(TitleBar), new PropertyMetadata(value));

    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public ImageSource? Icon { get => (ImageSource?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public double TitleSize { get => (double)GetValue(TitleSizeProperty); set => SetValue(TitleSizeProperty, value); }
    public bool DimTitle { get => (bool)GetValue(DimTitleProperty); set => SetValue(DimTitleProperty, value); }
    public double GlyphSize { get => (double)GetValue(GlyphSizeProperty); set => SetValue(GlyphSizeProperty, value); }
    public bool ShowMaximise { get => (bool)GetValue(ShowMaximiseProperty); set => SetValue(ShowMaximiseProperty, value); }
    public bool ShowClose { get => (bool)GetValue(ShowCloseProperty); set => SetValue(ShowCloseProperty, value); }
    public string? CloseToolTip { get => (string?)GetValue(CloseToolTipProperty); set => SetValue(CloseToolTipProperty, value); }
    public string CloseName { get => (string)GetValue(CloseNameProperty); set => SetValue(CloseNameProperty, value); }
    public bool IsMaximised => (bool)GetValue(IsMaximisedProperty);

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (Window.GetWindow(this) is not { } window) return;
        if (ReadLocalValue(TitleProperty) == DependencyProperty.UnsetValue)
            SetBinding(TitleProperty, new Binding(nameof(Window.Title)) { Source = window });
        SetValue(IsMaximisedKey, window.WindowState == WindowState.Maximized);
        window.StateChanged += (_, _) => SetValue(IsMaximisedKey, window.WindowState == WindowState.Maximized);
        if (GetTemplateChild("PART_Close") is Button close) close.Click += (_, _) => SystemCommands.CloseWindow(window);
        if (GetTemplateChild("PART_Maximise") is Button max)
            max.Click += (_, _) =>
            {
                if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
                else SystemCommands.MaximizeWindow(window);
            };
    }
}
