using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Settings;
using Serilog;
using Provenance.Common;
using Provenance.Common.Game.Windower;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Settings;

public sealed record AddonsTarget(ThirdPartyHook Platform, string? Folder, string Profile);

public delegate Task<WindowerInstallResult> WindowerInstaller(string windowerDir, string branch, AddonKind kind, string name);

public sealed record AddonBadge(string Text, string Tip, bool Warn = false);

internal sealed record PageWork(AddonsPage Page, string? Note = null, Exception? Failure = null);

public partial class AddonsPluginsViewModel : ObservableObject
{
    private const string SandboxText =
        "With sandbox on, the game only gets the game settings saved in this boot configuration. Set them on the Game page and save them there.";

    private readonly ISettingsService _settings;
    private readonly Func<AddonsTarget> _target;
    private readonly WindowerInstaller _install;
    private readonly AddonsHeading _addonsHeading = new(), _pluginsHeading = new(),
        _polHeading = new("Plugins Ashita loads into PlayOnline before the game starts."), _end = new();
    // one header per group for good, a new one each time would cost it the keyboard focus on every rebuild
    private readonly List<AddonsSection> _sections;
    private AddonsTarget? _shown;
    private AddonsScan? _scan;
    private AddonsPage? _page;
    private int _refreshes, _tab;
    private bool _visible, _useGlobal;

    public AddonsPluginsViewModel(ISettingsService settings, Func<AddonsTarget> target, WindowerInstaller? install = null)
    {
        _settings = settings;
        _target = target;
        _install = install ?? InstallFromWindower;
        _sections = AddonsPageBuilder.Groups.All.Select(g => new AddonsSection(this, g)).ToList();
        Items.Add(this);
    }

    public ObservableCollection<AddonRow> Addons { get; } = [];
    public ObservableCollection<AddonRow> Plugins { get; } = [];
    public ObservableCollection<AddonRow> PolPlugins { get; } = [];

    public ObservableCollection<object> Items { get; } = [];

    public ObservableCollection<string> Tabs { get; } = ["Addons", "Plugins"];

    public int Tab
    {
        get => _tab;
        set
        {
            if (!SetProperty(ref _tab, Math.Clamp(value, 0, Tabs.Count - 1))) return;
            OnPropertyChanged(nameof(PageTitle));
            Filter();
        }
    }

    public string PageTitle => HasLists ? Tabs[Tab] : "Addons and plugins";

    [ObservableProperty] private string _search = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLists), nameof(PageTitle))] private string? _message = "";
    [ObservableProperty] private string _intro = "";
    [ObservableProperty] private string? _problem;
    public StatusLine Status { get; } = new();
    [ObservableProperty] private bool _isWindower;
    [ObservableProperty] private bool _isAshita;
    [ObservableProperty] private ThirdPartyHook? _cardPlatform;
    [ObservableProperty] private string _cardHeading = "";
    [ObservableProperty] private string _cardName = "";
    [ObservableProperty] private bool _canChangeUseGlobal = true;
    [ObservableProperty] private string? _noAddons;
    [ObservableProperty] private string? _noPlugins;
    [ObservableProperty] private string? _noPolPlugins;
    [ObservableProperty] private bool _sameListForEveryProfile;

    public bool HasLists => Message is null;

    partial void OnNoAddonsChanged(string? value) => _addonsHeading.Note = value;
    partial void OnNoPluginsChanged(string? value) => _pluginsHeading.Note = value;

    private void SyncTabs(AddonsPage page)
    {
        if (page.Message is not null) return;
        if (IsAshita)
        {
            if (Tabs.Count == 2) Tabs.Add("PlayOnline Plugins");
        }
        else if (Tabs.Count == 3)
        {
            Tab = 0;
            Tabs.RemoveAt(2);
        }
    }

    internal Task Refreshing { get; private set; } = Task.CompletedTask;

    internal Func<Func<AddonsScan?>, Task<AddonsScan?>> RunScanForTest { get; set; } = work => Task.Run(work);

    internal Func<Func<PageWork>, Task<PageWork>> RunPageForTest { get; set; } = work => Task.Run(work);

    internal Task Changing { get; private set; } = Task.CompletedTask;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(UseGlobalEnabled))] private bool _busy;

    public bool UseGlobalEnabled => CanChangeUseGlobal && !Busy;

    partial void OnBusyChanged(bool value)
    {
        foreach (var r in Addons.Concat(Plugins).Concat(PolPlugins)) r.OwnerBusyChanged();
    }

    partial void OnCanChangeUseGlobalChanged(bool value) => OnPropertyChanged(nameof(UseGlobalEnabled));

    partial void OnSearchChanged(string value) => Filter();

    public void VisibleChanged(bool visible)
    {
        _visible = visible;
        if (visible) Refreshing = RefreshAsync();
    }

    internal void TargetChanged()
    {
        if (_visible) Refreshing = RefreshAsync();
    }

    internal async Task RefreshAsync()
    {
        int mine = ++_refreshes;
        var target = _target();
        if (target != _shown)
        {
            _shown = null;
            _scan = null;
            _page = null;
            Status.Clear();
            Problem = null;
            IsWindower = IsAshita = false;
            Addons.Clear();
            Plugins.Clear();
            PolPlugins.Clear();
            Message = "Looking for addons and plugins...";
            SameListForEveryProfile = target.Platform == ThirdPartyHook.Windower
                                      && _settings.Current.WindowerAddonLists?.GetValueOrDefault(target.Profile) is not { UseGlobal: false };
            Filter();
        }
        AddonsScan? scan;
        try { scan = await RunScanForTest(() => AddonsPageBuilder.Scan(target)); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the addons and plugins in {Folder}", target.Folder);
            if (mine != _refreshes) return;
            (_shown, _scan) = (target, null);
            Apply(AddonsPage.Empty(ErrorText.Of(ex, "Provenance couldn't read the addons and plugins in that folder.")));
            return;
        }
        if (mine != _refreshes) return;
        (_shown, _scan) = (target, scan);
        await ReloadAsync(mine);
    }

    private async Task ReloadAsync(int mine)
    {
        if (_shown is not { } target || Busy) return;
        var scan = _scan;
        var work = await RunPageForTest(() => new PageWork(BuildPage(target, scan)));
        if (mine != _refreshes || Busy) return;
        Apply(work.Page);
    }

    private AddonsPage BuildPage(AddonsTarget target, AddonsScan? scan)
    {
        try { return AddonsPageBuilder.Build(target, Lists(), _settings.Current.WindowerSharedExtras?.Copy(), scan); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the addons and plugins in {Folder}", target.Folder);
            return AddonsPage.Empty(ErrorText.Of(ex, "Provenance couldn't read the addons and plugins in that folder."));
        }
    }

    private async Task ChangeAsync(AddonsTarget target, Func<string?> change, Func<Exception, string> failure)
    {
        int mine = _refreshes;
        var scan = _scan;
        Status.Clear();
        Busy = true;
        PageWork done;
        try
        {
            done = await RunPageForTest(() =>
            {
                string? note = null;
                Exception? error = null;
                try { note = change(); }
                catch (Exception ex) { error = ex; }
                return new PageWork(BuildPage(target, scan), note, error);
            });
        }
        finally { Busy = false; }
        if (done.Failure is { } ex) Status.Fail(failure(ex));
        else if (done.Note is { } note) Status.Show(note);
        if (mine == _refreshes && ReferenceEquals(scan, _scan)) Apply(done.Page);
        else await ReloadAsync(_refreshes);
    }

    private Dictionary<string, WindowerAddonList> Lists() =>
        (_settings.Current.WindowerAddonLists ?? []).ToDictionary(kv => kv.Key, kv => kv.Value?.Copy()!, StringComparer.Ordinal);

    private void Apply(AddonsPage page)
    {
        _page = page;
        Message = page.Message;
        Intro = page.Intro;
        Problem = page.Problem == Status.Text ? null : page.Problem;
        IsWindower = page.Message is null && page.Platform == ThirdPartyHook.Windower;
        IsAshita = page.Message is null && page.Platform == ThirdPartyHook.Ashita;
        SyncTabs(page);
        CardPlatform = page.Message is null ? page.Platform : null;
        SameListForEveryProfile = IsWindower && page.UseGlobal;
        CardHeading = IsAshita ? "Addons and plugins for the Ashita boot configuration"
                    : SameListForEveryProfile ? "Addons and plugins for Windower" : "Addons and plugins for the Windower profile";
        CardName = SameListForEveryProfile ? "Every profile" : page.Display;
        _useGlobal = page.UseGlobal;
        OnPropertyChanged(nameof(UseGlobal));
        CanChangeUseGlobal = !(page.UseGlobal && page.ListLocked);
        Fill(Addons, page.Addons);
        Fill(Plugins, page.Plugins);
        Fill(PolPlugins, page.PolPlugins);
        Filter();
    }

    private void Fill(ObservableCollection<AddonRow> rows, List<RowData> data)
    {
        var old = new Dictionary<string, AddonRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) old.TryAdd(r.Key, r);
        var next = data.Select(d => old.TryGetValue(d.Listing.Key, out var r) ? r.Update(d) : new AddonRow(this, d)).ToList();
        if (next.SequenceEqual(rows)) return;
        rows.Clear();
        foreach (var r in next) rows.Add(r);
    }

    private void Filter()
    {
        var words = Search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var r in Addons.Concat(Plugins).Concat(PolPlugins)) r.Visible = r.Matches(words);
        NoAddons = Nothing(Addons, _page?.NoAddons);
        NoPlugins = Nothing(Plugins, _page?.NoPlugins);
        NoPolPlugins = Nothing(PolPlugins, _page?.NoPolPlugins);
        _polHeading.Note = NoPolPlugins ?? (PolPlugins.Any(r => r.Visible && r.Incompatible) ? AshitaInstall.IncompatibleNote : null);
        _polHeading.Warning = PolPlugins.Any(r => string.Equals(r.Key, "sandbox", StringComparison.OrdinalIgnoreCase) && r.On) ? SandboxText : null;
        List<object> items = [this];
        if (HasLists)
        {
            var (heading, rows) = Tab switch { 1 => (_pluginsHeading, Plugins), 2 => (_polHeading, PolPlugins), _ => (_addonsHeading, Addons) };
            items.Add(heading);
            var shown = rows.Where(r => r.Visible).ToList();
            foreach (var section in _sections)
            {
                var inside = shown.Where(r => r.Group == section.Group).ToList();
                if (inside.Count == 0) continue;
                section.Count = inside.Count;
                items.Add(section);
                if (SectionsOpen) items.AddRange(inside);
            }
            items.AddRange(shown.Where(r => r.Group is null));
            items.Add(_end);
        }
        Sync(Items, items);
    }

    internal bool SectionsOpen => !_settings.Current.AddonSectionsFolded;

    internal void FoldSections(bool folded)
    {
        if (_settings.Current.AddonSectionsFolded == folded) return;
        _settings.SaveChange(s => s.AddonSectionsFolded = folded);
        foreach (var s in _sections) s.FoldChanged();
        Filter();
    }

    private static void Sync(ObservableCollection<object> items, List<object> next)
    {
        var keep = new HashSet<object>(next, ReferenceEqualityComparer.Instance);
        for (int i = items.Count - 1; i >= 0; i--)
            if (!keep.Contains(items[i])) items.RemoveAt(i);
        for (int i = 0; i < next.Count; i++)
        {
            if (i < items.Count && ReferenceEquals(items[i], next[i])) continue;
            int at = items.IndexOf(next[i]);
            if (at > i) items.Move(at, i);
            else items.Insert(i, next[i]);
        }
    }

    private static string? Nothing(ObservableCollection<AddonRow> rows, string? none) =>
        rows.Count == 0 ? none : rows.Any(r => r.Visible) ? null : "Nothing here matches your search.";

    public bool UseGlobal
    {
        get => _useGlobal;
        set
        {
            if (value == _useGlobal) return;
            if (!Busy && _shown is { Platform: ThirdPartyHook.Windower, Folder: { } dir } target && _page is { } page)
            {
                _useGlobal = value;
                Changing = ChangeAsync(target, () => { AddonSwitches.SetUseGlobal(_settings, dir, page.Key, value); return null; }, ex =>
                {
                    Log.Warning(ex, "Could not change whether {Profile} uses the shared list", page.Key);
                    return ErrorText.Of(ex, "Provenance couldn't save that change.");
                });
            }
            OnPropertyChanged();
        }
    }

    internal bool Switch(AddonRow row, bool on)
    {
        if (Busy || _shown is not { Folder: { } dir } target || _page is not { } page) return false;
        var offered = (_scan as WindowerScan)?.Offered(row.Kind, row.Key);
        Changing = ChangeAsync(target, () =>
        {
            if (target.Platform == ThirdPartyHook.Windower) AddonSwitches.SwitchWindower(_settings, dir, page.Key, row.Kind, row.Key, on, offered);
            else if (target.Platform == ThirdPartyHook.Ashita) return AddonSwitches.SwitchAshita(dir, target.Profile, row.Kind, row.Key, row.Name, on);
            return null;
        }, ex =>
        {
            Log.Warning(ex, "Could not turn {Name} {State}", row.Key, on ? "on" : "off");
            return ErrorText.Of(ex, "Provenance couldn't save that change. If the file is open in another program, close it and try again.");
        });
        return true;
    }

    [RelayCommand]
    private async Task Install(AddonRow? row)
    {
        if (row is null || _shown is not { Platform: ThirdPartyHook.Windower, Folder: { } dir } || _page is not { } page) return;
        row.Installing = true;
        Status.Show($"Installing {row.Name}...");
        try
        {
            var result = await _install(dir, page.Branch, row.Kind, row.Key);
            if (result.Installed) Status.Show($"{row.Name} is installed.");
            else
            {
                Log.Warning("Could not install {Name}: {Errors}", row.Key, string.Join(" | ", result.Errors));
                Status.Fail($"{row.Name} couldn't be installed. {result.Errors.FirstOrDefault() ?? "Windower's servers didn't send it."}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not install {Name}", row.Key);
            Status.Fail(ErrorText.Of(ex, $"{row.Name} couldn't be installed. Try again in a moment."));
        }
        finally { row.Installing = false; }
        Refreshing = RefreshAsync();
        await Refreshing;
    }

    internal Action<string, bool> ShowInExplorerForTest { get; set; } = (path, isFile) => { if (isFile) Explorer.ShowFile(path); else Explorer.ShowFolder(path); };

    [RelayCommand]
    private void OpenFolder(AddonRow? row)
    {
        if (row?.Location is not { } path) return;
        bool isFile = row.Kind != AddonKind.Addon;
        if (isFile ? !File.Exists(path) : !Directory.Exists(path))
        {
            Status.Fail($"{row.Name} isn't installed any more.");
            Refreshing = RefreshAsync();
            return;
        }
        try { ShowInExplorerForTest(path, isFile); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Path} in Explorer", path);
            Status.Fail("Windows couldn't open that folder.");
        }
    }

    internal Action<AddonDocs> OpenDocsForTest { get; set; } = DocsOpener.Open;
    internal Action<string, string, bool> ShowReadmeForTest { get; set; } = DocsOpener.ShowText;

    [RelayCommand]
    private void OpenDocs(AddonRow? row)
    {
        if (row?.Docs is not { } docs) return;
        if (docs.Kind != AddonDocsKind.Link && !File.Exists(docs.Target))
        {
            Status.Fail($"{row.Name}'s documentation isn't there any more.");
            Refreshing = RefreshAsync();
            return;
        }
        if (docs.Kind == AddonDocsKind.Text)
        {
            if (ReadmeFiles.ReadText(docs.Target) is { } text) ShowReadmeForTest($"{row.Name} documentation", text, ReadmeFiles.IsMarkdown(docs.Target));
            else Status.Fail($"Provenance couldn't read {row.Name}'s documentation.");
            return;
        }
        try { OpenDocsForTest(docs); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open the documentation for {Name}", row.Key);
            Status.Fail($"Windows couldn't open {row.Name}'s documentation.");
        }
    }

    private static Task<WindowerInstallResult> InstallFromWindower(string dir, string branch, AddonKind kind, string name)
    {
        var updater = new WindowerUpdater(dir, branch);
        return kind == AddonKind.Plugin ? updater.InstallPluginAsync(name) : updater.InstallAddonAsync(name);
    }
}
