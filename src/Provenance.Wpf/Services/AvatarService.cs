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
                                  Func<string, ProfilePictureCrop>? cropOf = null)
{
    private readonly string _extras = extrasDir ?? ProfilePictureExtras.Folder;
    private string? _viewerDir;
    private ProfilePictureCatalog.Catalog? _offered;
    private int _offeredGeneration = -1;
    private bool _askedForExtras;
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
            var (x, y, w, h) = ProfilePictureSheets.Cell(picId);
            var sheet = LoadSheet(ProfilePictureSheets.SheetOf(picId));
            if (sheet != null && x + w <= sheet.PixelWidth && y + h <= sheet.PixelHeight)
            {
                var crop = new CroppedBitmap(sheet, new Int32Rect(x, y, w, h));
                crop.Freeze();
                ProfilePictureCrops.Attach(crop, setKey is null ? ProfilePictureCrop.Default : (cropOf ?? (k => ProfilePictureCrop.For(k)))(setKey));
                img = crop;
            }
        }
        catch { }
        lock (_lock) _cache[picId] = img;
        return img;
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
