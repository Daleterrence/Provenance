using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.Controls;

// POL's online badges, a coloured dot if they ever fail to load
public sealed class StatusMark : ContentControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(StatusMark),
        new PropertyMetadata("", (d, _) => ((StatusMark)d).Rebuild()));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(StatusMark),
        new PropertyMetadata(true, (d, _) => ((StatusMark)d).Rebuild()));

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
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

    internal static string Key(string label) => label switch
    {
        "Online" => "online",
        "Away" => "away",
        "Busy" => "busy",
        "Appear offline" => "invisible",
        "" => "",
        _ => "offline",
    };

    private void Rebuild()
    {
        var key = Key(Status ?? "");
        if (key.Length == 0) { Content = null; Visibility = Visibility.Collapsed; return; }
        Visibility = Visibility.Visible;
        // POL draws no badge for offline, the space keeps the text lined up
        if (key == "offline") { Content = null; return; }
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
