using System.Runtime.CompilerServices;
using System.Windows.Media;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.Services;

// the crop rides along with the image, so every circle can find its set's without being told
public static class ProfilePictureCrops
{
    private static readonly ConditionalWeakTable<ImageSource, ProfilePictureCrop> Table = new();

    internal static void Attach(ImageSource image, ProfilePictureCrop crop) => Table.AddOrUpdate(image, crop);

    public static ProfilePictureCrop Of(ImageSource? image) =>
        image is not null && Table.TryGetValue(image, out var crop) ? crop : ProfilePictureCrop.Default;
}
