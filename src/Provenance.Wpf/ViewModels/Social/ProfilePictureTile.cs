using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.ViewModels.Social;

public sealed partial class ProfilePictureTile(ProfilePictureCatalog.ProfilePicture pic, ImageSource? image)
    : ObservableObject
{
    public ProfilePictureCatalog.ProfilePicture ProfilePicture { get; } = pic;
    public ImageSource? Image { get; } = image;
    public string? Tip => string.Join("\n", new[] { ProfilePicture.Name, ProfilePicture.Artist }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } tip
        ? tip : null;
    [ObservableProperty] private bool _isSelected;
}
