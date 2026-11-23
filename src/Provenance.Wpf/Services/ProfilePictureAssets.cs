using System.IO;
using System.Windows.Media.Imaging;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.Services;

public sealed record ProfilePictureAsset(BitmapSource Image, ProfilePictureAssetMeta Meta);

// Assets\profile-pictures\<id>.png or .jpg, placed by the numbers in pictures.json beside them
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

    internal static IEnumerable<int> Ids
    {
        get
        {
            return Meta().Keys;
        }
    }

    public static ProfilePictureAsset? Find(int picId)
    {
        if (!Meta().TryGetValue(picId, out var meta)) return null;
        return Open(picId, name =>
        {
            try
            {
                var image = new BitmapImage(new Uri(Root + name));
                image.Freeze();
                return image;
            }
            catch { return null; }
        }) is { } found ? new ProfilePictureAsset(found, meta) : null;
    }

    // the two with transparency are png, the rest jpg
    internal static BitmapImage? Open(int picId, Func<string, BitmapImage?> load) =>
        load($"{picId}.png") ?? load($"{picId}.jpg");
}
