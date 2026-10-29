using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Provenance.Wpf.Views;

// POL's own icon from the user's install (none of SE's icons ship with Provenance), else a coloured dot
public sealed class StatusMark : ContentControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(StatusMark),
        new PropertyMetadata("", (d, _) => ((StatusMark)d).Rebuild()));

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public static Func<string, ImageSource?>? IconSource { get; set; }
    public static Func<bool>? IconsAvailable { get; set; }

    public StatusMark()
    {
        Width = 16;
        Height = 16;
        VerticalAlignment = VerticalAlignment.Center;
        Rebuild();
    }

    private static string Key(string label) => label switch
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
        if (IconSource?.Invoke(key) is { } icon)
        {
            Content = new Image { Source = icon, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            return;
        }
        if (IconsAvailable?.Invoke() == true)
        {
            // POL draws no icon for offline, the space keeps the text lined up
            Content = null;
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
