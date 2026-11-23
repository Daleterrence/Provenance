using System.Windows.Media.Imaging;

namespace Provenance.Wpf.Services;

public static class NationEmblems
{
    private static readonly Dictionary<string, BitmapImage?> Loaded = [];

    public static BitmapImage? Image(string? key)
    {
        // reading a pack address before the app exists fixes WPF's resource assembly
#pragma warning disable RS0030
        if (key is null || System.Windows.Application.Current is null) return null;
#pragma warning restore RS0030
        lock (Loaded)
        {
            if (Loaded.TryGetValue(key, out var have)) return have;
            BitmapImage? image = null;
            try
            {
                image = new BitmapImage(new Uri($"pack://application:,,,/Provenance;component/Assets/nation-emblems/{key}.png"));
                image.Freeze();
            }
            catch { image = null; }
            return Loaded[key] = image;
        }
    }
}
