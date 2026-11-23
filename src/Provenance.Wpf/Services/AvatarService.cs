using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Provenance.Common.Install;
using Provenance.Common.PolFormat;
using Provenance.Common.Settings;
using Serilog;

namespace Provenance.Wpf.Services;

public sealed class AvatarService(ISettingsService settings, Func<string, ProfilePictureCatalog.Catalog?>? catalog = null,
                                  string? extrasDir = null, Action<string>? needExtras = null,
                                  Func<string, ProfilePictureCrop>? cropOf = null, Func<int, ProfilePictureAsset?>? assetOf = null)
{
    private readonly string _extras = extrasDir ?? ProfilePictureExtras.Folder;
    private string? _viewerDir;
    private ProfilePictureCatalog.Catalog? _offered;
    private int _offeredGeneration = -1;
    private bool _askedForExtras;
    private readonly HashSet<int> _unkeyed = [];
    private readonly object _lock = new();
    private readonly Dictionary<int, ImageSource?> _cache = new();
    private readonly Dictionary<int, BitmapSource?> _sheets = new();

    internal void UseStandIns(string viewerDir, IReadOnlyDictionary<int, ImageSource?> pictures)
    {
        _viewerDir = viewerDir;
        lock (_lock)
            foreach (var (id, picture) in pictures) _cache[id] = picture;
    }

    private string? ViewerDir
    {
        get
        {
            if (_viewerDir is not null) return _viewerDir;
            var dir = settings.DetectedRegion is { } r ? PolPaths.TryGetInstallFolder(r, GameProduct.Viewer) : null;
            if (dir is not null && Interlocked.CompareExchange(ref _viewerDir, dir, null) is null)
                Log.Information("Profile pictures: PlayOnline Viewer folder {Dir}", dir);
            return _viewerDir;
        }
    }

    public ImageSource? NoProfilePicture => Load(0);

    public ImageSource? ProfilePicture(int picId) =>
        (picId > 0 && Offered(picId) is { } offer ? Load(picId, offer.SetKey) : null) ?? NoProfilePicture;

    // with no catalogue to go by, every picture is drawn as it always was
    private (bool Yes, string? SetKey)? Offered(int picId)
    {
        if (ViewerDir is not { } dir) return (true, null);
        if (!_askedForExtras)
        {
            _askedForExtras = true;
            needExtras?.Invoke(dir);
        }
        var generation = ProfilePictureExtras.Generation;
        bool stale;
        lock (_lock) stale = _offeredGeneration != generation;
        if (stale)
        {
            var loaded = (catalog ?? (d => ProfilePictureCatalog.Load(d, _extras)))(dir);
            lock (_lock)
            {
                _offered = loaded;
                _offeredGeneration = generation;
                foreach (var gone in _cache.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) _cache.Remove(gone);
                foreach (var gone in _unkeyed) _cache.Remove(gone);
                _unkeyed.Clear();
                foreach (var gone in _sheets.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) _sheets.Remove(gone);
            }
        }
        lock (_lock)
        {
            if (_offered is not { } offered) return (true, null);
            return offered.Find(picId) is { } pic ? (true, pic.CategoryKey) : null;
        }
    }

    public void Prewarm(IEnumerable<int> picIds)
    {
        try
        {
            if (ViewerDir is null || picIds is null) return;
            var sheets = picIds.Where(id => id >= 0).Select(id => ProfilePictureSheets.SheetOf(id)).Distinct().ToArray();
            if (sheets.Length == 0) return;
            _ = Task.Run(() =>
            {
                foreach (var s in sheets)
                {
                    try { LoadSheet(s); }
                    catch { }
                }
            });
        }
        catch { }
    }

    private ImageSource? Load(int picId, string? setKey = null)
    {
        if (picId < 0 || ViewerDir is null) return null;
        lock (_lock) if (_cache.TryGetValue(picId, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            var circle = setKey is null ? ProfilePictureCrop.Default : (cropOf ?? (k => ProfilePictureCrop.For(k)))(setKey);
            if ((assetOf ?? ProfilePictureAssets.Find)(picId) is { } asset)
                img = Circle(asset.Image, asset.Meta.X0, asset.Meta.Y0, asset.Meta.Scale, circle);
            else
            {
                var (x, y, w, h) = ProfilePictureSheets.Cell(picId);
                var sheet = LoadSheet(ProfilePictureSheets.SheetOf(picId));
                if (sheet != null && x + w <= sheet.PixelWidth && y + h <= sheet.PixelHeight)
                    img = Circle(new CroppedBitmap(sheet, new Int32Rect(x, y, w, h)), 0, 0, 1, circle);
            }
        }
        catch { }
        lock (_lock)
        {
            _cache[picId] = img;
            if (setKey is null) _unkeyed.Add(picId);
            else _unkeyed.Remove(picId);
        }
        return img;
    }

    private static ImageSource? Circle(BitmapSource image, double x0, double y0, double scale, ProfilePictureCrop circle)
    {
        var cover = circle.Within(x0, y0, image.PixelWidth / scale, image.PixelHeight / scale);
        if (cover is null) return null;
        int x = (int)Math.Round((cover.X - x0) * scale), y = (int)Math.Round((cover.Y - y0) * scale);
        int size = Math.Max(1, (int)Math.Round(cover.Size * scale));
        x = Math.Clamp(x, 0, image.PixelWidth - 1);
        y = Math.Clamp(y, 0, image.PixelHeight - 1);
        size = Math.Min(size, Math.Min(image.PixelWidth - x, image.PixelHeight - y));
        var crop = new CroppedBitmap(image, new Int32Rect(x, y, size, size));
        crop.Freeze();
        return crop;
    }

    private BitmapSource? LoadSheet(int sheetIndex)
    {
        if (ViewerDir is not { } dir) return null;
        lock (_lock) if (_sheets.TryGetValue(sheetIndex, out var cached)) return cached;
        BitmapSource? img = null;
        try
        {
            if (ProfilePictureSheets.SheetPath(dir, _extras, sheetIndex) is { } sheetPath) img = DecodeSheet(sheetPath);
        }
        catch { }
        lock (_lock)
        {
            if (_sheets.TryGetValue(sheetIndex, out var won)) return won;
            _sheets[sheetIndex] = img;
        }
        return img;
    }

    internal static BitmapSource DecodeSheet(string path)
    {
        var sheet = new BitmapImage();
        sheet.BeginInit();
        sheet.StreamSource = new MemoryStream(ProfilePictureSheets.Decode(File.ReadAllBytes(path)));
        sheet.CacheOption = BitmapCacheOption.OnLoad;
        sheet.EndInit();
        sheet.Freeze();
        return sheet;
    }
}
