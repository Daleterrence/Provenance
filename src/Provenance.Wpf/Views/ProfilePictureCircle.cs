using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.Views;

// a POL profile pic in a circle. The 64x96 portrait fills the width and sits a bit high, a fifth off the top
public sealed class ProfilePictureCircle : ContentControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(ProfilePictureCircle),
        new PropertyMetadata(null, (d, _) => ((ProfilePictureCircle)d).Rebuild()));

    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(
        nameof(Diameter), typeof(double), typeof(ProfilePictureCircle),
        new PropertyMetadata(36.0, (d, _) => ((ProfilePictureCircle)d).Rebuild()));

    public static readonly DependencyProperty InGameProperty = DependencyProperty.Register(
        nameof(InGame), typeof(bool), typeof(ProfilePictureCircle),
        new PropertyMetadata(false, (d, _) => ((ProfilePictureCircle)d).Rebuild()));

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    // a friend in FFXI gets the XI badge, tucked behind the top right so just the letters show
    public bool InGame
    {
        get => (bool)GetValue(InGameProperty);
        set => SetValue(InGameProperty, value);
    }

    internal Image? Badge { get; private set; }

    public ProfilePictureCircle()
    {
        Focusable = false;
        Rebuild();
    }

    private void Rebuild()
    {
        double d = Diameter;
        Width = Height = d;
        var clip = new EllipseGeometry(new Point(d / 2, d / 2), d / 2, d / 2);
        var grid = new Grid { Width = d, Height = d, Clip = clip };
        grid.SetResourceReference(Panel.BackgroundProperty, "Surface.2");
        var img = new Image
        {
            Source = Source,
            Width = d,
            Height = d * 1.5,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -d * 0.2, 0, 0),
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        grid.Children.Add(img);
        Badge = null;
        if (!InGame || SvgIcons.XiBadge is not { } badge)
        {
            Content = grid;
            return;
        }
        // a Canvas, so the bit hanging off the edge isn't clipped by the layout
        double w = d * 0.64;
        Badge = new Image { Source = badge, Width = w, Height = w * badge.Height / badge.Width, Stretch = Stretch.Uniform };
        // its top edge level with the picture's (the icon starts a unit into its 20 high box)
        Canvas.SetLeft(Badge, d * 0.62);
        Canvas.SetTop(Badge, -Badge.Height / 20);
        var under = new Canvas();
        under.Children.Add(Badge);
        var both = new Grid { Width = d, Height = d };
        both.Children.Add(under);
        both.Children.Add(grid);
        Content = both;
    }
}
