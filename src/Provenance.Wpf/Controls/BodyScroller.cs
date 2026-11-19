using System.Windows;
using System.Windows.Controls;

namespace Provenance.Wpf.Controls;

public class BodyScroller : ScrollViewer
{
    public bool Fills { get; set; }

    public BodyScroller()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Focusable = false;
        ScrollChanged += (_, e) =>
        {
            if (Fills && e.ViewportHeightChange != 0 && Content is FrameworkElement body) body.MinHeight = e.ViewportHeight;
        };
    }
}
