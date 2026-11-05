using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.Views.Social;

public sealed partial class ProfilePictureTile(ProfilePictureCatalog.ProfilePicture pic, ImageSource? image)
    : ObservableObject
{
    public ProfilePictureCatalog.ProfilePicture ProfilePicture { get; } = pic;
    public ImageSource? Image { get; } = image;
    public string Tip => string.IsNullOrEmpty(ProfilePicture.Artist)
        ? ProfilePicture.Name : $"{ProfilePicture.Name}\n{ProfilePicture.Artist}";
    [ObservableProperty] private bool _isSelected;
}
