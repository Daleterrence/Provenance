using System.Windows;
using System.Windows.Controls;

namespace Provenance.Wpf.Controls;

// the part of a window above its buttons, scrolling when the window's been cut to the work area. Only fills the space in
// a fixed-height window, one that sizes to its content could never shrink again
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
