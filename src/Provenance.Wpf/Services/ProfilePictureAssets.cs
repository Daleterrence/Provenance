using System.IO;
using System.Windows.Media.Imaging;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.Services;

public sealed record ProfilePictureAsset(BitmapSource Image, ProfilePictureAssetMeta Meta);

// Assets\profile-pictures\<id>.png, placed by the numbers in pictures.json beside them
public static class ProfilePictureAssets
{
    private const string Root = "pack://application:,,,/Provenance;component/Assets/profile-pictures/";
    private static readonly Lazy<Dictionary<int, ProfilePictureAssetMeta>> Meta = new(() =>
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri(Root + "pictures.json"));
            if (info is null) return [];
            using var reader = new StreamReader(info.Stream);
            return ProfilePictureAssetMeta.Parse(reader.ReadToEnd());
        }
        catch { return []; }
    });

    public static ProfilePictureAsset? Find(int picId)
    {
        if (!Meta.Value.TryGetValue(picId, out var meta)) return null;
        try
        {
            var image = new BitmapImage(new Uri($"{Root}{picId}.png"));
            image.Freeze();
            return new ProfilePictureAsset(image, meta);
        }
        catch { return null; }
    }
}
