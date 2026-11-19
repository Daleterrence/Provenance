using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Provenance.Wpf.Services;
using Provenance.Wpf.Controls.Behaviours;

namespace Provenance.Wpf.Controls;

public sealed class SvgIcon : Grid
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SvgIcon), new PropertyMetadata("", (d, _) => ((SvgIcon)d).Refresh()));

    public static readonly DependencyProperty IconFileProperty = DependencyProperty.Register(
        nameof(IconFile), typeof(string), typeof(SvgIcon), new PropertyMetadata("", (d, _) => ((SvgIcon)d).Refresh()));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(SvgIcon), new PropertyMetadata(false, (d, _) => ((SvgIcon)d).Refresh()));

    public static readonly DependencyProperty FlatProperty = DependencyProperty.Register(
        nameof(Flat), typeof(bool), typeof(SvgIcon), new PropertyMetadata(false, (d, _) => ((SvgIcon)d).Refresh()));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(SvgIcon), new PropertyMetadata(false, (d, _) => ((SvgIcon)d).Refresh()));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(SvgIcon),
        new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.Inherits, (d, _) => ((SvgIcon)d).Refresh()));

    internal static readonly DependencyProperty HoverProperty = DependencyProperty.Register(
        "Hover", typeof(bool), typeof(SvgIcon), new PropertyMetadata(false, (d, _) => ((SvgIcon)d).Refresh()));

    internal static readonly DependencyProperty FocusedProperty = DependencyProperty.Register(
        "Focused", typeof(bool), typeof(SvgIcon), new PropertyMetadata(false, (d, _) => ((SvgIcon)d).Refresh()));

    internal static readonly DependencyProperty PressedProperty = DependencyProperty.Register(
        "Pressed", typeof(bool), typeof(SvgIcon), new PropertyMetadata(false, (d, e) => ((SvgIcon)d).OnPressed((bool)e.NewValue)));

    public static readonly TimeSpan TransitionTime = TimeSpan.FromMilliseconds(120);

    private readonly DispatcherTimer _settle;
    private bool _settling, _onButton;
    private readonly Ellipse _disc = new(), _press = new() { Opacity = 0.5 }, _light = new() { Fill = Brushes.White }, _edge = new();
    private readonly Image _assetIcon = new() { Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0.5, 0.5) };

    public ImageSource? Source => _assetIcon.Source;
    internal bool HasDisc => _disc.Visibility == Visibility.Visible;
    internal Brush? DiscFill => _disc.Fill;
    internal double DiscSize => _disc.Width;
    internal double AssetIconScale => _assetIcon.RenderTransform is ScaleTransform s ? s.ScaleX : 1;

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string IconFile
    {
        get => (string)GetValue(IconFileProperty);
        set => SetValue(IconFileProperty, value);
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public bool Flat
    {
        get => (bool)GetValue(FlatProperty);
        set => SetValue(FlatProperty, value);
    }

    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IconState State { get; private set; }

    // defaults rather than set in the constructor, where they'd beat the sizes a DataTemplate gives it
    static SvgIcon()
    {
        WidthProperty.OverrideMetadata(typeof(SvgIcon), new FrameworkPropertyMetadata(20.0));
        HeightProperty.OverrideMetadata(typeof(SvgIcon), new FrameworkPropertyMetadata(20.0));
        VerticalAlignmentProperty.OverrideMetadata(typeof(SvgIcon), new FrameworkPropertyMetadata(VerticalAlignment.Center));
    }

    public SvgIcon()
    {
        _edge.Stroke = Brushes.White;
        _press.SetResourceReference(Shape.FillProperty, "Btn.Fill.Press");
        foreach (var e in new[] { _disc, _press, _light, _edge })
        {
            e.HorizontalAlignment = HorizontalAlignment.Center;
            e.VerticalAlignment = VerticalAlignment.Center;
            e.Visibility = Visibility.Collapsed;
            e.IsHitTestVisible = false;
            Children.Add(e);
        }
        Children.Add(_assetIcon);
        _assetIcon.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty,
                              new Binding { Path = new PropertyPath("(AutomationProperties.Name)"), Source = this });
        SizeChanged += (_, _) => SizeDisc();
        _settle = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TransitionTime };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _settling = false;
            Refresh();
        };
        Loaded += (_, _) => FollowButton();
    }

    public static IconState Resolve(bool selected, bool pressed, bool settling, bool open) =>
        pressed ? (settling ? IconState.Transition : IconState.Active)
        : open ? IconState.Active
        : selected ? IconState.Selected
        : IconState.Idle;

    public static void OpenWhile(object? button, Action dialog)
    {
        var icon = button is DependencyObject d ? Find(d) : null;
        if (icon is not null) icon.IsOpen = true;
        try { dialog(); }
        finally { if (icon is not null) icon.IsOpen = false; }
    }

    private static SvgIcon? Find(DependencyObject root)
    {
        if (root is SvgIcon icon) return icon;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (Find(child) is { } found) return found;
        return null;
    }

    private void FollowButton()
    {
        if (Flat || BindingOperations.IsDataBound(this, HoverProperty)) return;
        DependencyObject? at = this;
        while (at is not null and not ButtonBase) at = VisualTreeHelper.GetParent(at) ?? LogicalTreeHelper.GetParent(at);
        if (at is not ButtonBase button) return;
        _onButton = true;
        SetBinding(HoverProperty, new Binding(nameof(IsMouseOver)) { Source = button });
        SetBinding(FocusedProperty, new Binding { Path = new PropertyPath(FocusCue.ShownProperty), Source = button });
        SetBinding(PressedProperty, new Binding(nameof(ButtonBase.IsPressed)) { Source = button });
        Refresh();
    }

    private void OnPressed(bool pressed)
    {
        _settle.Stop();
        _settling = pressed;
        if (pressed) _settle.Start();
        Refresh();
    }

    private void Refresh()
    {
        State = Resolve((bool)GetValue(HoverProperty) || (bool)GetValue(FocusedProperty), (bool)GetValue(PressedProperty), _settling, IsOpen);
        _assetIcon.Opacity = 1;
        if (IconFile.Length > 0)
        {
            var colour = Foreground as SolidColorBrush;
            _assetIcon.Source = SvgIcons.TintedFile(IconFile, colour?.Color ?? Colors.Black);
            _assetIcon.Opacity = colour?.Opacity ?? 1;
            _assetIcon.RenderTransform = Transform.Identity;
            _disc.Visibility = _light.Visibility = _edge.Visibility = _press.Visibility = Visibility.Collapsed;
            return;
        }
        if (Flat)
        {
            var tint = Foreground as SolidColorBrush;
            _assetIcon.Source = Glyph.Length == 0 ? null : SvgIcons.Tinted(Glyph, tint?.Color ?? Colors.Black, Filled);
            _assetIcon.Opacity = tint?.Opacity ?? 1;
        }
        else _assetIcon.Source = Glyph.Length == 0 ? null : _onButton ? SvgIcons.ButtonGlyph(Glyph, State) : SvgIcons.Glyph(Glyph, State);
        double scale = Flat || Glyph.Length == 0 ? 1 : _onButton ? SvgIcons.ButtonScale(Glyph) : SvgIcons.GlyphScale(Glyph);
        _assetIcon.RenderTransform = scale == 1 ? Transform.Identity : new ScaleTransform(scale, scale);
        var shown = _onButton && !Flat && Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _disc.Visibility = _light.Visibility = _edge.Visibility = shown;
        _press.Visibility = _onButton && !Flat && State == IconState.Transition ? Visibility.Visible : Visibility.Collapsed;
        if (!_onButton || Flat) return;
        _disc.SetResourceReference(Shape.FillProperty, State switch
        {
            IconState.Selected or IconState.Transition => "Btn.Fill.Hover",
            IconState.Active => "Btn.Icon.Active",
            _ => "Btn.Fill",
        });
        _light.Opacity = State switch { IconState.Selected => 0.32, IconState.Transition => 0.26, IconState.Active => 0, _ => 0.2 };
        _edge.Opacity = State switch { IconState.Selected => 0.65, IconState.Transition => 0.5, IconState.Active => 0.8, _ => 0.45 };
        SizeDisc();
    }

    private void SizeDisc()
    {
        if (Glyph.Length == 0) return;
        double d = Math.Min(ActualWidth, ActualHeight) * SvgIcons.DiscShare;
        foreach (var e in new[] { _disc, _press, _light, _edge }) e.Width = e.Height = d;
        _edge.StrokeThickness = d / 20;
    }
}
