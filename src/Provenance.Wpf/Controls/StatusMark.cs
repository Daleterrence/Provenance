using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Provenance.Common.Social;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.Controls;

public sealed class StatusMark : ContentControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(PolStatus), typeof(StatusMark),
        new PropertyMetadata(PolStatus.Online, (d, _) => ((StatusMark)d).Rebuild()));

    public static readonly DependencyProperty OnlineProperty = DependencyProperty.Register(
        nameof(Online), typeof(bool), typeof(StatusMark),
        new PropertyMetadata(true, (d, _) => ((StatusMark)d).Rebuild()));

    public static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        nameof(Shown), typeof(bool), typeof(StatusMark),
        new PropertyMetadata(true, (d, _) => ((StatusMark)d).Rebuild()));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(StatusMark),
        new PropertyMetadata(true, (d, _) => ((StatusMark)d).Rebuild()));

    public PolStatus Status
    {
        get => (PolStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public bool Online
    {
        get => (bool)GetValue(OnlineProperty);
        set => SetValue(OnlineProperty, value);
    }

    public bool Shown
    {
        get => (bool)GetValue(ShownProperty);
        set => SetValue(ShownProperty, value);
    }

    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    public StatusMark()
    {
        Width = 16;
        Height = 16;
        VerticalAlignment = VerticalAlignment.Center;
        Rebuild();
    }

    internal static string Key(PolStatus status) => status switch
    {
        PolStatus.Online => "online",
        PolStatus.Away => "away",
        PolStatus.Busy => "busy",
        _ => "invisible",
    };

    private void Rebuild()
    {
        if (!Shown) { Content = null; Visibility = Visibility.Collapsed; return; }
        Visibility = Visibility.Visible;
        if (!Online) { Content = null; return; }
        var key = Key(Status);
        if (SvgIcons.Online(key, Filled) is { } icon)
        {
            Content = new Image { Source = icon, Stretch = Stretch.Uniform };
            return;
        }
        var dot = new Ellipse { Width = 9, Height = 9 };
        dot.SetResourceReference(Shape.FillProperty, key switch
        {
            "online" => "State.Online",
            "away" => "State.Warn",
            "busy" => "State.Down",
            _ => "Text.3",
        });
        Content = dot;
    }
}
