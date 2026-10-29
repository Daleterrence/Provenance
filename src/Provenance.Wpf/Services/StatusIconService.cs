using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Provenance.Wpf.Services;

// cells in the viewer's sheet: empty (offline), pulse (online and busy), "?" (appear offline), "..." (away)
public static class StatusIconService
{
    private static readonly Dictionary<string, ImageSource?> Icons = new();

    public static bool Available { get; private set; }

    public static void Load(string? viewerDir)
    {
        if (viewerDir is null) return;
        try
        {
            var path = Path.Combine(viewerDir, "viewer", "data", "system", "skins", "onlinestatus_mini.png");
            if (!File.Exists(path)) return;
            var sheet = AvatarService.DecodeSheet(path);
            if (sheet.PixelWidth < 80 || sheet.PixelHeight < 20) return;
            ImageSource Cell(int i)
            {
                var c = new CroppedBitmap(sheet, new Int32Rect(i * 20, 0, 20, 20));
                c.Freeze();
                return c;
            }
            Icons["online"] = Cell(1);
            Icons["busy"] = Cell(1);
            Icons["invisible"] = Cell(2);
            Icons["away"] = Cell(3);
            Icons["offline"] = null;
            Available = true;
        }
        catch { Available = false; }
    }

    public static ImageSource? Get(string key) => Icons.TryGetValue(key, out var img) ? img : null;
}
