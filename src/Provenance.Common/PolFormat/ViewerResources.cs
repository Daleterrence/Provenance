using Provenance.Common.Game;
using Provenance.Common.Install;
using Provenance.Common.Settings;

namespace Provenance.Common.PolFormat;

public sealed class ViewerResources
{
    private readonly ISettingsService _settings;
    private readonly Func<PolRegion, string?> _folder;
    private readonly Func<string, IReadOnlyDictionary<int, string>?> _strings;
    private readonly Func<string, PolLanguageFilter?> _filter;
    private readonly Func<string, ProfilePictureCatalog.Catalog?> _pictures;
    private IReadOnlyDictionary<int, string>? _table;
    private PolLanguageFilter? _wordFilter;
    private ProfilePictureCatalog.Catalog? _catalog;
    private bool _tableTried, _wordFilterTried;

    public ViewerResources(ISettingsService settings, Func<PolRegion, string?>? folder = null,
                             Func<string, IReadOnlyDictionary<int, string>?>? strings = null,
                             Func<string, PolLanguageFilter?>? filter = null,
                             Func<string, ProfilePictureCatalog.Catalog?>? pictures = null)
    {
        _settings = settings;
        _folder = folder ?? (region => PolPaths.TryGetInstallFolder(region, GameProduct.Viewer));
        _strings = strings ?? PolStringTable.Load;
        _filter = filter ?? PolLanguageFilter.Load;
        _pictures = pictures ?? (dir => ProfilePictureCatalog.Load(dir));
    }

    public string? Folder => _settings.DetectedRegion is { } r ? _folder(r) : null;

    public string Text(int id, string fallback)
    {
        if (!_tableTried)
        {
            _tableTried = true;
            _table = Strings();
        }
        return _table != null && _table.TryGetValue(id, out var s) && s.Length > 0 ? s : fallback;
    }

    // the JP table is 100 entries shorter than the US one, so an ID nobody's read there gets the US words
    public string TextOutsideJapan(int id, string us) => _settings.DetectedRegion == PolRegion.Japan ? us : Text(id, us);

    public IReadOnlyDictionary<int, string>? Strings() => Folder is { } viewer ? _strings(viewer) : null;

    public bool FilterOn => PolOptions.ChatFilter(_settings.Current);

    public string Filtered(string text)
    {
        if (text.Length == 0 || !FilterOn) return text;
        if (!_wordFilterTried && _settings.DetectedRegion != null)
        {
            _wordFilterTried = true;
            _wordFilter = Folder is { } viewer ? _filter(viewer) : null;
        }
        return _wordFilter?.Apply(text) ?? text;
    }

    public ProfilePictureCatalog.Catalog? Pictures
    {
        get
        {
            if (_catalog is null && Folder is { } viewer) _catalog = _pictures(viewer);
            return _catalog;
        }
    }
}
