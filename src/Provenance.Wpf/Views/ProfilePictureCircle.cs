using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
        Content = grid;
    }
}
