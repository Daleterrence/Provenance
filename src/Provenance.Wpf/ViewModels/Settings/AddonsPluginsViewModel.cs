using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Security;
using Provenance.Common.Settings;
using Serilog;
using Provenance.Common;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.Windower;
using Provenance.Common.Hook;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.ViewModels.Settings;

public enum AddonKind { Addon, Plugin, PolPlugin }

// Folder is null when it can't be found. Profile is the Windower profile ("" is the unnamed one) or the boot config
public sealed record AddonsTarget(ThirdPartyHook Platform, string? Folder, string Profile);

public delegate Task<WindowerInstallResult> WindowerInstaller(string windowerDir, string branch, AddonKind kind, string name);

public sealed record AddonBadge(string Text, string Tip, bool Warn = false);

// Whatever the Windower/Ashita tab has picked. Every switch saves straight away, then the page is rebuilt from the
// files and Provenance's settings as they are now
public partial class AddonsPluginsViewModel : ObservableObject
{
    private const string LoadedBy = "already loaded by ";
    private const string SettingsNotSaved = "Provenance couldn't save your settings, so your changes will only last until Provenance closes.";
    private const string SandboxText =
        "With sandbox on, the game only gets the game settings saved in this boot configuration. Set them on the Game page and save them there.";

    private static readonly string[] Watched =
    [
        nameof(SettingsViewModel.Hook), nameof(SettingsViewModel.WindowerPath), nameof(SettingsViewModel.AshitaPath),
        nameof(SettingsViewModel.WindowerProfile), nameof(SettingsViewModel.AshitaBootConfig),
    ];

    private readonly ISettingsService _settings;
    private readonly Func<AddonsTarget> _target;
    private readonly WindowerInstaller _install;
    private readonly AddonsHeading _addonsHeading = new(), _pluginsHeading = new(),
        _polHeading = new("Plugins Ashita loads into PlayOnline before the game starts."), _end = new();
    private AddonsTarget? _shown;
    private object? _scan;
    private Page? _page;
    private int _refreshes, _tab;
    private bool _visible, _useGlobal;

    public AddonsPluginsViewModel(ISettingsService settings, Func<AddonsTarget> target, WindowerInstaller? install = null)
    {
        _settings = settings;
        _target = target;
        _install = install ?? InstallFromWindower;
        Items.Add(this);
    }

    public AddonsPluginsViewModel(ISettingsService settings, SettingsViewModel tab) : this(settings, () => TargetOf(tab, settings.Current)) =>
        tab.PropertyChanged += (_, e) => { if (Watched.Contains(e.PropertyName)) TargetChanged(); };

    // the same folder and profile the Windower/Ashita tab is changing. With nothing in its list (settings.xml won't
    // read, say) it's still the one that was picked
    internal static AddonsTarget TargetOf(SettingsViewModel tab, LauncherSettings saved) => tab.Hook switch
    {
        ThirdPartyHook.Ashita => new(ThirdPartyHook.Ashita, tab.AshitaDir, tab.AshitaBootConfig?.Name ?? saved.AshitaBootConfig ?? ""),
        ThirdPartyHook.Windower => new(ThirdPartyHook.Windower, tab.WindowerDir, tab.WindowerProfile?.Name ?? PlayTarget.For(saved, null, ThirdPartyHook.Windower).Profile),
        _ => new(ThirdPartyHook.None, null, ""),
    };

    public ObservableCollection<AddonRow> Addons { get; } = [];
    public ObservableCollection<AddonRow> Plugins { get; } = [];
    public ObservableCollection<AddonRow> PolPlugins { get; } = [];

    // top to bottom: this for the top of the page, then the sub-tab's list under its heading. Only rows come and go,
    // so the search box isn't rebuilt mid-typing
    public ObservableCollection<object> Items { get; } = [];

    public ObservableCollection<string> Tabs { get; } = ["Addons", "Plugins"];

    // the same order as AddonKind
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
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isWindower;
    [ObservableProperty] private bool _isAshita;
    [ObservableProperty] private ThirdPartyHook? _cardPlatform;
    [ObservableProperty] private string _cardHeading = "";
    [ObservableProperty] private string _cardName = "";
    [ObservableProperty] private bool _canChangeUseGlobal = true;
    [ObservableProperty] private string? _noAddons;
    [ObservableProperty] private string? _noPlugins;
    [ObservableProperty] private string? _noPolPlugins;
    // Windower's shared list, so Settings hides the profile switcher. While a page loads it's a guess from the
    // settings, or the switcher would pop up and vanish again
    [ObservableProperty] private bool _sameListForEveryProfile;

    public bool HasLists => Message is null;

    partial void OnNoAddonsChanged(string? value) => _addonsHeading.Note = value;
    partial void OnNoPluginsChanged(string? value) => _pluginsHeading.Note = value;

    // Ashita's POL plugins tab goes with it, but not while a page is loading, so another boot config keeps it
    // picked. Addons gets picked before it goes, or the strip would be left with nothing
    private void SyncTabs(Page page)
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

    // the tests swap this for one that holds the scan back
    internal Func<Func<object?>, Task<object?>> RunScan { get; set; } = work => Task.Run(work);

    partial void OnSearchChanged(string value) => Filter();

    public void VisibleChanged(bool visible)
    {
        _visible = visible;
        if (visible) Refreshing = RefreshAsync();
    }

    private void TargetChanged()
    {
        if (_visible) Refreshing = RefreshAsync();
    }

    // only the scan's in the background. The newest one builds the page from everything as it is when it lands, so a
    // switch flipped meanwhile stays put
    internal async Task RefreshAsync()
    {
        int mine = ++_refreshes;
        var target = _target();
        if (target != _shown)
        {
            _shown = null;
            _scan = null;
            _page = null;
            Status = "";
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
        object? scan;
        try { scan = await RunScan(() => Scan(target)); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the addons and plugins in {Folder}", target.Folder);
            if (mine != _refreshes) return;
            (_shown, _scan) = (target, null);
            Apply(Page.Empty(ErrorText.Of(ex, "Provenance couldn't read the addons and plugins in that folder.")));
            return;
        }
        if (mine != _refreshes) return;
        (_shown, _scan) = (target, scan);
        Reload();
    }

    private void Reload()
    {
        if (_shown is not { } target) return;
        Page page;
        try { page = Build(target, Lists(), _settings.Current.WindowerSharedExtras?.Copy(), _scan); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the addons and plugins in {Folder}", target.Folder);
            page = Page.Empty(ErrorText.Of(ex, "Provenance couldn't read the addons and plugins in that folder."));
        }
        Apply(page);
    }

    private Dictionary<string, WindowerAddonList> Lists() =>
        (_settings.Current.WindowerAddonLists ?? []).ToDictionary(kv => kv.Key, kv => kv.Value?.Copy()!, StringComparer.Ordinal);

    private void Apply(Page page)
    {
        _page = page;
        Message = page.Message;
        Intro = page.Intro;
        Problem = page.Problem == Status ? null : page.Problem;
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

    // rows that are still there keep their place on screen, so a switch doesn't jump about once it's saved
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
        _polHeading.Warning = PolPlugins.Any(r => Same(r.Key, "sandbox") && r.On) ? SandboxText : null;
        // hidden rows stay out of the list altogether, a collapsed one still costs the panel a container
        List<object> items = [this];
        if (HasLists)
        {
            var (heading, rows) = Tab switch { 1 => (_pluginsHeading, Plugins), 2 => (_polHeading, PolPlugins), _ => (_addonsHeading, Addons) };
            items.Add(heading);
            items.AddRange(rows.Where(r => r.Visible));
            items.Add(_end);
        }
        Sync(Items, items);
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

    private void ShowError(string text)
    {
        Status = text;
        StatusIsError = true;
    }

    private void ShowNote(string text)
    {
        Status = text;
        StatusIsError = false;
    }

    public bool UseGlobal
    {
        get => _useGlobal;
        set
        {
            if (value == _useGlobal) return;
            if (_shown is { Platform: ThirdPartyHook.Windower, Folder: { } dir } && _page is { } page)
            {
                ShowNote("");
                try { SetUseGlobal(dir, page.Key, value); }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not change whether {Profile} uses the shared list", page.Key);
                    ShowError(ErrorText.Of(ex, "Provenance couldn't save that change."));
                }
                Reload();
            }
            OnPropertyChanged();
        }
    }

    // the first time it's turned off the own list starts as a copy of the shared one, after that it comes back as it was
    private void SetUseGlobal(string dir, string key, bool value)
    {
        var lists = _settings.Current.WindowerAddonLists ??= [];
        lists.TryGetValue(key, out var own);
        if (value)
        {
            if (own is null) return;
            own.UseGlobal = true;
        }
        else
        {
            // a settings.xml that didn't read is an empty list, and that'd stick as the profile's own
            var file = Readable(dir);
            if (own is null) lists[key] = own = WindowerAddonList.Global(file, _settings.Current.WindowerSharedExtras);
            own.UseGlobal = false;
        }
        SaveSettings();
    }

    // Save swallows its errors, only the first of a run comes out as SaveFailed. The change still holds until Provenance
    // closes, so the page keeps it and says so
    private void SaveSettings()
    {
        bool failed = false;
        void Failed(string _) => failed = true;
        _settings.SaveFailed += Failed;
        try { _settings.Save(_settings.Current); }
        finally { _settings.SaveFailed -= Failed; }
        if (failed) throw Shown.Mark(new IOException(SettingsNotSaved));
    }

    private static WindowerSettingsFile Readable(string dir)
    {
        var file = WindowerSettingsFile.Load(dir);
        return file.LoadError is not null ? throw Shown.Mark(new InvalidOperationException(Unreadable(file))) : file;
    }

    private static string Unreadable(WindowerSettingsFile file)
    {
        Log.Warning("Windower's settings.xml couldn't be read: {Why}", file.LoadError);
        return file.Unreadable("choose Start a new settings file under Windower/Ashita to set it aside and start again. Until then its list can't be changed here.");
    }

    internal void Switch(AddonRow row, bool on)
    {
        if (_shown is not { Folder: { } dir } target || _page is not { } page) return;
        ShowNote("");
        try
        {
            if (target.Platform == ThirdPartyHook.Windower) SwitchWindower(dir, page.Key, row, on);
            else if (target.Platform == ThirdPartyHook.Ashita) SwitchAshita(dir, target.Profile, row, on);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not turn {Name} {State}", row.Key, on ? "on" : "off");
            ShowError(ErrorText.Of(ex, "Provenance couldn't save that change. If the file is open in another program, close it and try again."));
        }
        Reload();
    }

    private void SwitchWindower(string dir, string key, AddonRow row, bool on)
    {
        var lists = _settings.Current.WindowerAddonLists ??= [];
        if (lists.TryGetValue(key, out var own) && own is { UseGlobal: false })
        {
            Toggle(row.Kind == AddonKind.Addon ? own.Addons : own.Plugins, row.Key, on);
            SaveSettings();
            return;
        }
        var file = Readable(dir);
        var extras = _settings.Current.WindowerSharedExtras ??= new WindowerAddonList();
        var extraNames = row.Kind == AddonKind.Addon ? extras.Addons ??= [] : extras.Plugins ??= [];
        var offered = OfferedByWindower(row);
        if (on ? offered is not null : Has(row.Kind == AddonKind.Addon ? file.Addons : file.Plugins, row.Key))
            WindowerSettingsFile.Update(dir, f => Toggle(row.Kind == AddonKind.Addon ? f.Addons : f.Plugins, offered ?? row.Key, on));
        if (on ? offered is null : Has(extraNames, row.Key))
        {
            Toggle(extraNames, row.Key, on);
            SaveSettings();
        }
    }

    // settings.xml only ever gets names Windower.exe offers itself, spelt its way. Everything else on the shared list
    // stays in Provenance's own settings
    private string? OfferedByWindower(AddonRow row) => _scan is not WindowerScan scan ? null
        : row.Kind == AddonKind.Addon ? scan.Tree?.Addons.FirstOrDefault(a => Same(a.Name, row.Key))?.Name
        : scan.Plugins.FirstOrDefault(p => p.InManifest && Same(p.Listing.Key, row.Key))?.Listing.Key;

    private static bool Has(List<string> names, string name) => names.Any(n => Same(n, name));

    private static void Toggle(List<string> names, string name, bool on)
    {
        bool Same(string n) => string.Equals(n, name, StringComparison.OrdinalIgnoreCase);
        if (!on) names.RemoveAll(Same);
        else if (!names.Any(Same)) names.Add(name);
    }

    private void SwitchAshita(string dir, string bootName, AddonRow row, bool on)
    {
        var path = BootPath(dir, bootName);
        var boot = AshitaBootConfig.Load(path);
        if (row.Kind == AddonKind.PolPlugin)
        {
            boot.SetPolPlugin(row.Key, on);
            boot.Save(path);
            return;
        }
        bool Change(AshitaScript s) => row.Kind == AddonKind.Addon ? s.SetAddon(row.Key, on) : s.SetPlugin(row.Key, on);
        var name = boot.Script;
        var current = OpenScript(dir, name);
        // tried on the script as it is first, so nothing gets copied for a change that can't be made anyway
        if (!Change(current ?? AshitaScript.FromBytes([])))
            throw Shown.Mark(new InvalidOperationException(string.IsNullOrWhiteSpace(name)
                ? $"Provenance couldn't change {row.Name} for this boot configuration."
                : $"Provenance couldn't change {row.Name} in the script {AshitaScript.FileName(name)}."));
        if (current is not null && !AshitaScript.NeedsCopy(dir, name, bootName, Scripts(dir, name)))
        {
            current.Save();
            return;
        }
        var copyName = AshitaScript.CopyFor(dir, name, bootName);
        var copyPath = AshitaScript.PathFor(dir, copyName)!;
        try
        {
            var copy = AshitaScript.Open(dir, copyName)!;
            Change(copy);
            copy.Save();
            boot.Script = copyName;
            boot.Save(path);
        }
        catch
        {
            // nothing points at the copy, so it goes rather than every retry leaving another one behind
            if (!PointsAt(path, copyName))
                foreach (var f in (string[])[copyPath, copyPath + ".bak"])
                    try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        Log.Information("Gave the Ashita boot config {Boot} its own script {Script}", bootName, copyName);
        ShowNote(string.IsNullOrWhiteSpace(name)
            ? $"This boot configuration now has its own script, {copyName}."
            : $"This boot configuration now has its own script, {copyName} (a copy of {AshitaScript.FileName(name)}), so {AshitaScript.FileName(name)} stays as it is.");
    }

    // one that can't be read back keeps the copy, a boot config pointing at a missing script is worse than a spare file
    private static bool PointsAt(string bootPath, string script)
    {
        try { return string.Equals(AshitaBootConfig.Load(bootPath).Script, script, StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    private static string BootPath(string dir, string bootName) => Path.Combine(AshitaBootFiles.Folder(dir), bootName);

    private static AshitaScript? OpenScript(string dir, string? name) =>
        string.IsNullOrWhiteSpace(name) ? AshitaScript.FromBytes([], Path.Combine(dir, "scripts")) : AshitaScript.Open(dir, name);

    // a boot config that can't be read counts as sharing the script, a needless copy is harmless and a missed one isn't
    private static List<(string Boot, string? Script)> Scripts(string dir, string? fallback) =>
        AshitaBootFiles.List(dir).Select(n =>
        {
            try { return (n, AshitaBootConfig.Load(BootPath(dir, n)).Script); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (n, fallback); }
        }).ToList();

    // asked of an empty script and a blank boot config, which take any name Ashita can load
    private static bool Loadable(AddonKind kind, string name)
    {
        if (kind != AddonKind.PolPlugin) return AshitaScript.FromBytes([]).SetPlugin(name, true);
        try
        {
            new AshitaBootConfig(AshitaIniFile.FromText("")).SetPolPlugin(name, true);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    [RelayCommand]
    private async Task Install(AddonRow? row)
    {
        if (row is null || _shown is not { Platform: ThirdPartyHook.Windower, Folder: { } dir } || _page is not { } page) return;
        row.Installing = true;
        ShowNote($"Installing {row.Name}...");
        try
        {
            var result = await _install(dir, page.Branch, row.Kind, row.Key);
            if (result.Installed) ShowNote($"{row.Name} is installed.");
            else
            {
                Log.Warning("Could not install {Name}: {Errors}", row.Key, string.Join(" | ", result.Errors));
                ShowError($"{row.Name} couldn't be installed. {result.Errors.FirstOrDefault() ?? "Windower's servers didn't send it."}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not install {Name}", row.Key);
            ShowError(ErrorText.Of(ex, $"{row.Name} couldn't be installed. Try again in a moment."));
        }
        finally { row.Installing = false; }
        Refreshing = RefreshAsync();
        await Refreshing;
    }

    // a test swaps it for one that just notes the path
    internal Action<string, bool> ShowInExplorer { get; set; } = (path, isFile) => { if (isFile) Explorer.ShowFile(path); else Explorer.ShowFolder(path); };

    // an addon opens its own folder, a plugin its folder with the DLL picked
    [RelayCommand]
    private void OpenFolder(AddonRow? row)
    {
        if (row?.Location is not { } path) return;
        bool isFile = row.Kind != AddonKind.Addon;
        if (isFile ? !File.Exists(path) : !Directory.Exists(path))
        {
            ShowError($"{row.Name} isn't installed any more.");
            Refreshing = RefreshAsync();
            return;
        }
        try { ShowInExplorer(path, isFile); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Path} in Explorer", path);
            ShowError("Windows couldn't open that folder.");
        }
    }

    private static Task<WindowerInstallResult> InstallFromWindower(string dir, string branch, AddonKind kind, string name)
    {
        var updater = new WindowerUpdater(dir, branch);
        return kind == AddonKind.Plugin ? updater.InstallPluginAsync(name) : updater.InstallAddonAsync(name);
    }

    private static object? Scan(AddonsTarget target) => target switch
    {
        { Platform: ThirdPartyHook.Windower, Folder: { } dir } when !FolderPins.Linked(dir) =>
            new WindowerScan(WindowerPlugins.Scan(dir), WindowerAddons.Scan(dir), WindowerAddonTree.Load(WindowerAddonTree.CachePath(dir))),
        { Platform: ThirdPartyHook.Ashita, Folder: { } dir } when !string.IsNullOrWhiteSpace(target.Profile) && File.Exists(BootPath(dir, target.Profile)) =>
            new AshitaScan(AshitaAddons.Scan(dir), AshitaPlugins.Scan(dir)),
        _ => null,
    };

    private static Page Build(AddonsTarget target, IReadOnlyDictionary<string, WindowerAddonList> lists, WindowerAddonList? extras, object? scan) => target switch
    {
        { Platform: ThirdPartyHook.Windower, Folder: null } =>
            Page.Empty("Provenance couldn't find Windower. Choose its folder under Windower/Ashita first."),
        { Platform: ThirdPartyHook.Windower, Folder: { } dir } when FolderPins.Linked(dir) => Page.Empty(WindowerInstall.LinkedText),
        { Platform: ThirdPartyHook.Windower, Folder: { } dir } => BuildWindower(dir, target.Profile, lists, extras, scan as WindowerScan),
        { Platform: ThirdPartyHook.Ashita, Folder: null } =>
            Page.Empty("Provenance couldn't find Ashita. Choose its folder under Windower/Ashita first."),
        { Platform: ThirdPartyHook.Ashita, Folder: { } dir } => BuildAshita(dir, target.Profile, scan as AshitaScan),
        _ => Page.Empty("Choose Windower or Ashita and its folder under Windower/Ashita first."),
    };

    private sealed record WindowerScan(IReadOnlyList<WindowerPluginInfo> Plugins, IReadOnlyList<WindowerAddonInfo> Addons, WindowerAddonTree? Tree);

    private static Page BuildWindower(string dir, string profileName, IReadOnlyDictionary<string, WindowerAddonList> lists, WindowerAddonList? extras,
                                      WindowerScan? scan)
    {
        scan ??= (WindowerScan)Scan(new AddonsTarget(ThirdPartyHook.Windower, dir, profileName))!;
        var page = new Page { Platform = ThirdPartyHook.Windower };
        WindowerSettingsFile file;
        bool locked = false;
        try { file = WindowerSettingsFile.Load(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Could not read Windower's settings.xml");
            file = WindowerSettingsFile.Empty();
            locked = true;
            page.Problem = "Provenance couldn't read Windower's settings.xml, so its list can't be changed right now. Close anything that has it open, then come back to this page.";
        }
        if (file.LoadError is not null)
        {
            locked = true;
            page.Problem = Unreadable(file);
        }
        var profile = file.Find(profileName);
        page.Key = profile?.Name ?? profileName;
        page.Display = WindowerInstall.DisplayName(page.Key, file.Profiles.Select(p => p.Name).ToList());
        page.Branch = file.Branch;
        page.Intro = "Turn addons and plugins on or off. Changes save straight away.";
        var effective = WindowerAddonList.Effective(file, lists, page.Key, extras);
        page.UseGlobal = effective.UseGlobal;
        page.ListLocked = locked;
        profile ??= new WindowerProfile();
        bool frozen = locked && effective.UseGlobal;

        bool Listed(List<string> names, string name) => names.Contains(name, StringComparer.OrdinalIgnoreCase);
        var plugins = scan.Plugins.Where(p => !p.Required && (!p.Hidden || p.Installed || Listed(effective.Plugins, p.Listing.Key))).ToList();
        var missingPlugins = Missing(effective.Plugins, scan.Plugins.Select(p => p.Listing.Key));
        var pluginNames = plugins.Select(p => p.Listing.Key).Concat(missingPlugins).ToList();
        var installed = scan.Addons.Select(a => a.Listing.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notInstalled = (scan.Tree?.Addons ?? []).Where(t => !installed.Contains(t.Name)).ToList();
        var missingAddons = Missing(effective.Addons, scan.Addons.Select(a => a.Listing.Key).Concat(notInstalled.Select(t => t.Name)));
        var addonNames = scan.Addons.Select(a => a.Listing.Key).Concat(notInstalled.Select(t => t.Name)).Concat(missingAddons).ToList();

        var loadedPlugins = Reasons(dir, profile, scan, pluginNames, []);
        var loadedAddons = Reasons(dir, profile, scan, [], addonNames);

        RowData Row(AddonKind kind, AddonListing listing, bool isInstalled, bool installable, bool windowerPlugin = true, bool official = false)
        {
            var key = listing.Key;
            var on = Listed(kind == AddonKind.Addon ? effective.Addons : effective.Plugins, key);
            var badges = new List<AddonBadge>();
            bool canTurnOn = true, hasSwitch = true;
            if (!isInstalled) badges.Add(installable ? Badges.Install : Badges.NotInWindower);
            else if (!windowerPlugin)
            {
                badges.Add(Badges.NotAPlugin);
                canTurnOn = false;
            }
            var loaded = kind == AddonKind.Addon ? loadedAddons : loadedPlugins;
            if (loaded.TryGetValue(key, out var why) && why.StartsWith(LoadedBy, StringComparison.Ordinal))
            {
                badges.Add(why.EndsWith("init.txt", StringComparison.Ordinal) ? Badges.InitTxt : Badges.InitCommand);
                hasSwitch = false;
            }
            return new RowData(kind, listing)
            {
                On = on, HasSwitch = hasSwitch, CanChange = !frozen && (canTurnOn || on), CanInstall = !isInstalled && installable, Badges = badges,
                Team = official ? "Windower" : null,
            };
        }

        var repo = (scan.Tree?.Addons ?? []).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        page.Plugins = plugins.Select(p => Row(AddonKind.Plugin, p.Listing, p.Installed, p.InManifest, p.IsWindowerPlugin, official: p.InManifest))
            .Concat(missingPlugins.Select(n => Row(AddonKind.Plugin, AddonListing.Named(n), false, false)))
            .OrderBy(r => r.Listing.Name, StringComparer.OrdinalIgnoreCase).ToList();
        page.Addons = scan.Addons.Select(a => Row(AddonKind.Addon, a.Listing, true, false, official: repo.Contains(a.Listing.Key)))
            .Concat(notInstalled.Select(t => Row(AddonKind.Addon, AddonListing.Named(t.Name), false, true, official: true)))
            .Concat(missingAddons.Select(n => Row(AddonKind.Addon, AddonListing.Named(n), false, false)))
            .OrderBy(r => r.Listing.Name, StringComparer.OrdinalIgnoreCase).ToList();
        page.NoAddons = $"No addons found in {Path.Combine(dir, "addons")}.";
        page.NoPlugins = $"No plugins found in {Path.Combine(dir, "plugins")}.";
        return page;
    }

    private static List<string> Missing(IEnumerable<string> listed, IEnumerable<string> known)
    {
        var seen = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        return listed.Where(n => !string.IsNullOrWhiteSpace(n) && seen.Add(n)).ToList();
    }

    // the launch's own skip reasons read back, so a badge never disagrees with what really loads. Bit cheeky, but
    // nothing else knows
    private static Dictionary<string, string> Reasons(string dir, WindowerProfile profile, WindowerScan scan, List<string> plugins, List<string> addons)
    {
        var list = new WindowerAddonList { Plugins = plugins, Addons = addons };
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var launch = WindowerAddonList.ForLaunch(dir, profile, list, scan.Plugins, scan.Addons);
        foreach (var s in launch.Skipped)
        {
            int at = s.LastIndexOf(" (", StringComparison.Ordinal);
            if (at > 0 && s.EndsWith(')')) reasons.TryAdd(s[..at], s[(at + 2)..^1]);
        }
        foreach (var n in launch.InitCommandAddons) reasons.TryAdd(n, LoadedBy + "the profile's init command");
        return reasons;
    }

    private sealed record AshitaScan(IReadOnlyList<AshitaAddon> Addons, IReadOnlyList<AshitaPlugin> Plugins);

    private static Page BuildAshita(string dir, string bootName, AshitaScan? scan)
    {
        if (string.IsNullOrWhiteSpace(bootName)) return Page.Empty("Choose a boot configuration under Windower/Ashita first.");
        var path = BootPath(dir, bootName);
        if (!File.Exists(path))
            return Page.Empty($"The boot configuration {bootName} isn't in Ashita's config\\boot folder any more. Choose another one under Windower/Ashita.");
        scan ??= new AshitaScan(AshitaAddons.Scan(dir), AshitaPlugins.Scan(dir));
        var page = new Page
        {
            Platform = ThirdPartyHook.Ashita, Key = bootName, Display = AshitaBootFiles.ShownName(dir, bootName),
            Intro = "Turn addons and plugins on or off for this boot configuration. Changes save straight away.",
        };
        AshitaBootConfig boot;
        try { boot = AshitaBootConfig.Load(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Could not read the Ashita boot config {Boot}", bootName);
            return Page.Empty($"Provenance couldn't read the boot configuration {bootName}. If it's open in another program, close it and come back to this page.");
        }
        bool readOnly = AshitaBootFiles.IsShippedExample(bootName);
        if (readOnly)
            page.Problem = HookFailures.AshitaExampleFile(page.Display);
        AshitaScript script;
        try
        {
            var opened = OpenScript(dir, boot.Script);
            if (opened is null) page.Problem ??= $"The script {boot.Script} isn't in Ashita's scripts folder, so Provenance can't read what it loads.";
            script = opened ?? AshitaScript.FromBytes([]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Could not read the Ashita script {Script}", boot.Script);
            script = AshitaScript.FromBytes([]);
            readOnly = true;
            page.Problem = ErrorText.Of(e, $"Provenance couldn't read the script {boot.Script}.");
        }

        RowData Row(AddonKind kind, AddonListing listing, AshitaLoad state, bool installed, AddonBadge? problem = null)
        {
            var key = listing.Key;
            var badges = new List<AddonBadge>();
            if (!installed) badges.Add(Badges.NotInAshita);
            if (problem is not null) badges.Add(problem);
            else if (installed && !Loadable(kind, key)) badges.Add(problem = Badges.BadName);
            if (state == AshitaLoad.ByScript) badges.Add(Badges.ByScript);
            bool on = state == AshitaLoad.On, canTurnOn = installed && problem is null;
            return new RowData(kind, listing)
            {
                On = on, HasSwitch = state != AshitaLoad.ByScript, CanChange = !readOnly && (canTurnOn || on), Badges = badges,
                Team = AshitaTeam(kind, key),
            };
        }

        List<RowData> Rows(AddonKind kind, IEnumerable<(AddonListing Listing, AddonBadge? Problem)> found, IEnumerable<string> loaded,
                           Func<string, AshitaLoad> state)
        {
            var list = found.ToList();
            return list.Select(f => Row(kind, f.Listing, state(f.Listing.Key), true, f.Problem))
                .Concat(Missing(loaded, list.Select(f => f.Listing.Key)).Select(n => Row(kind, AddonListing.Named(n), state(n), false)))
                .OrderBy(r => r.Listing.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        page.Addons = Rows(AddonKind.Addon,
            scan.Addons.Select(a => (a.Listing, a.NotForAshitaV4 ? Badges.NotForV4 : (AddonBadge?)null)),
            script.Addons.Select(a => a.Name), script.Addon);
        page.Plugins = Rows(AddonKind.Plugin,
            scan.Plugins.Where(p => p.Kind == AshitaPluginKind.Plugin)
                .Select(p => (p.Listing, p.ForOtherAshita ? Badges.OtherAshita : (AddonBadge?)null)),
            script.Plugins.Select(p => p.Name), script.Plugin);

        var pol = boot.PolPlugins;
        AshitaLoad PolState(string n) => pol.Any(p => Same(p.Name, n) && p.On) ? AshitaLoad.On : AshitaLoad.Off;
        var polFound = scan.Plugins.Where(p => p.Kind == AshitaPluginKind.PolPlugin).ToList();
        // a switched-off one shows off whatever the boot configuration says, as it is when Provenance starts the game
        var polRows = polFound.Select(p => AshitaPolPlugins.IsTakeover(p.Listing.Key)
            ? new RowData(AddonKind.PolPlugin, p.Listing with { Description = AshitaPolPlugins.IncompatibleReason(p.Listing.Key) })
                { CanChange = false, Incompatible = true, Badges = [Badges.Incompatible], Team = AshitaTeam(AddonKind.PolPlugin, p.Listing.Key) }
            : Row(AddonKind.PolPlugin, p.Listing, PolState(p.Listing.Key), true, p.ForOtherAshita ? Badges.OtherAshita : null)).ToList();
        var turnedOn = pol.Where(p => p.On && !AshitaPolPlugins.IsTakeover(p.Name)).Select(p => p.Name);
        polRows.AddRange(Missing(turnedOn, polFound.Select(p => p.Listing.Key))
            .Select(n => Row(AddonKind.PolPlugin, AddonListing.Named(n), AshitaLoad.On, false)));
        page.PolPlugins = polRows.OrderBy(r => r.Incompatible).ThenBy(r => r.Listing.Name, StringComparer.OrdinalIgnoreCase).ToList();

        page.NoAddons = $"No addons found in {Path.Combine(dir, "addons")}.";
        page.NoPlugins = $"No plugins found in {Path.Combine(dir, "plugins")}.";
        page.NoPolPlugins = $"No PlayOnline plugins found in {Path.Combine(dir, "polplugins")}.";
        return page;
    }

    private static string? AshitaTeam(AddonKind kind, string name) => kind switch
    {
        AddonKind.Addon => AshitaShipped.IsAddon(name),
        AddonKind.Plugin => AshitaShipped.IsPlugin(name),
        _ => AshitaShipped.IsPolPlugin(name),
    } ? "Ashita" : null;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed class Page
    {
        public string? Message;
        public string Intro = "";
        public string? Problem;
        public ThirdPartyHook Platform = ThirdPartyHook.None;
        public bool UseGlobal = true, ListLocked;
        public string Key = "", Display = "";
        public string Branch = "stable";
        public List<RowData> Addons = [], Plugins = [], PolPlugins = [];
        public string? NoAddons, NoPlugins, NoPolPlugins;

        public static Page Empty(string message) => new() { Message = message };
    }

    private static class Badges
    {
        public static readonly AddonBadge Install = new("Not installed", "Press Install to add it to your Windower folder.");
        public static readonly AddonBadge NotInWindower = new("Not installed", "It's turned on, but it isn't in your Windower folder.");
        public static readonly AddonBadge NotInAshita = new("Not installed", "It's named in this boot configuration, but it isn't in your Ashita folder.");
        public static readonly AddonBadge NotAPlugin = new("Not a Windower plugin",
            "This file in Windower's plugins folder isn't a Windower 4 plugin, so it can't be turned on.", Warn: true);
        public static readonly AddonBadge InitTxt = new("Loaded by your init.txt",
            "Windower's scripts\\init.txt loads this itself, so it loads no matter what the list says.");
        public static readonly AddonBadge InitCommand = new("Loaded by this profile's init command",
            "This profile's init command loads it itself, so it loads no matter what the list says.");
        public static readonly AddonBadge ByScript = new("Set by your script",
            "Your Ashita script loads or unloads this in a way Provenance can't safely change, so it's left as it is.");
        public static readonly AddonBadge NotForV4 = new("Not for Ashita v4",
            "This addon was written for Windower or an older Ashita, so it won't run in Ashita v4.", Warn: true);
        public static readonly AddonBadge OtherAshita = new("Built for a different Ashita",
            "This plugin was built for a different version of Ashita, so Ashita won't load it.", Warn: true);
        public static readonly AddonBadge Incompatible = new(AshitaInstall.IncompatibleText,
            "Provenance keeps this off whenever it starts the game. Your Ashita settings aren't changed.", Warn: true);
        public static readonly AddonBadge BadName = new("Name Ashita can't load",
            "Ashita can't load a name like this one from a script or boot configuration. Rename it to plain letters and numbers to use it.", Warn: true);
    }
}
