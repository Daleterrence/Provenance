using System.IO;
using System.Windows.Media.Imaging;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.Services;

public sealed record ProfilePictureAsset(BitmapSource Image, ProfilePictureAssetMeta Meta);

// Assets\profile-pictures\<id>.png, placed by the numbers in pictures.json beside them
public static class ProfilePictureAssets
{
    private const string Root = "pack://application:,,,/Provenance;component/Assets/profile-pictures/";
    private static Dictionary<int, ProfilePictureAssetMeta>? _meta;

    // reading a pack address before the app exists fixes WPF's resource assembly, so nothing's read until there is one
    private static Dictionary<int, ProfilePictureAssetMeta> Meta()
    {
        if (_meta is not null) return _meta;
#pragma warning disable RS0030
        if (System.Windows.Application.Current is null) return [];
#pragma warning restore RS0030
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri(Root + "pictures.json"));
            if (info is null) return _meta = [];
            using var reader = new StreamReader(info.Stream);
            return _meta = ProfilePictureAssetMeta.Parse(reader.ReadToEnd());
        }
        catch { return _meta = []; }
    }

    public static ProfilePictureAsset? Find(int picId)
    {
        if (!Meta().TryGetValue(picId, out var meta)) return null;
        try
        {
            var image = new BitmapImage(new Uri($"{Root}{picId}.png"));
            image.Freeze();
            return new ProfilePictureAsset(image, meta);
        }
        catch { return null; }
    }
}
