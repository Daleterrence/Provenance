using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Provenance.Common.Social;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.Controls;

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
        var crop = ProfilePictureCrops.Of(Source);
        var img = new Image
        {
            Source = Source,
            Width = d * crop.Zoom,
            Height = d * crop.Zoom * 1.5,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness((d - d * crop.Zoom) / 2 + d * crop.Left, d * crop.Top, 0, 0),
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        grid.Children.Add(img);
        Badge = null;
        if (!InGame || SvgIcons.XiBadgeTucked is not { } badge)
        {
            Content = grid;
            return;
        }
        var at = XiBadgeGeometry.For(d);
        Badge = new Image { Source = badge, Width = at.Width, Height = at.Height, Stretch = Stretch.Uniform };
        Canvas.SetLeft(Badge, at.Left);
        Canvas.SetTop(Badge, at.Top);
        var under = new Canvas();
        under.Children.Add(Badge);
        var both = new Grid { Width = d, Height = d };
        both.Children.Add(under);
        both.Children.Add(grid);
        Content = both;
    }
}
