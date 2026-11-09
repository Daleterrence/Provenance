using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class MyProfilePicture : UserControl
{
    public static readonly DependencyProperty DiameterProperty =
        DependencyProperty.Register(nameof(Diameter), typeof(double), typeof(MyProfilePicture), new PropertyMetadata(36.0));

    public MyProfilePicture() => InitializeComponent();

    public double Diameter
    {
        get => (double)GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    private void OnClick(object sender, MouseButtonEventArgs e) =>
        (DataContext as SocialViewModel)?.ChangeProfilePictureCommand.Execute(null);
}
