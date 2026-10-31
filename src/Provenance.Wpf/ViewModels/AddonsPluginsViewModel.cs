using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Provenance.Common;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.Windower;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.ViewModels;

public enum AddonKind { Addon, Plugin, PolPlugin }

// Folder is null when it can't be found. Profile is the Windower profile ("" is the unnamed one) or the boot config
public sealed record AddonsTarget(string Platform, string? Folder, string Profile);

public delegate Task<WindowerInstallResult> WindowerInstaller(string windowerDir, string branch, AddonKind kind, string name);

public sealed record AddonBadge(string Text, string Tip, bool Warn = false);

// Whatever the Windower/Ashita tab has picked. Every switch saves straight away, then the page is rebuilt from the
// files and Provenance's settings as they are now
public partial class AddonsPluginsViewModel : ObservableObject
{
    private const string LoadedBy = "already loaded by ", OwnListOnly = "only a profile's own list can load it";
    private const string SettingsNotSaved = "Provenance couldn't save your settings, so your changes will only last until Provenance closes.";

    private static readonly string[] Watched =
    [
        nameof(SettingsViewModel.ThirdPartyHookIndex), nameof(SettingsViewModel.WindowerPath), nameof(SettingsViewModel.AshitaPath),
        nameof(SettingsViewModel.WindowerProfile), nameof(SettingsViewModel.AshitaBootConfig),
    ];

    private readonly ISettingsService _settings;
    private readonly Func<AddonsTarget> _target;
    private readonly WindowerInstaller _install;
    private readonly AddonsHeading _addonsHeading = new("Addons"), _pluginsHeading = new("Plugins"),
        _polHeading = new("PlayOnline plugins", "Ashita's polplugins, which load into PlayOnline before the game starts."), _end = new(null);
    private AddonsTarget? _shown;
    private object? _scan;
    private Page? _page;
    private int _refreshes;
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
    internal static AddonsTarget TargetOf(SettingsViewModel tab, LauncherSettings saved) => tab.ThirdPartyHookIndex switch
    {
        1 => new(ThirdPartyHookService.Ashita, tab.AshitaDir, tab.AshitaBootConfig?.Name ?? saved.AshitaBootConfig ?? ""),
        2 => new(ThirdPartyHookService.Windower, tab.WindowerDir, tab.WindowerProfile?.Name ?? WindowerSettingsFile.OldCopySource(saved.WindowerProfile)),
        _ => new(ThirdPartyHookService.None, null, ""),
    };

    public ObservableCollection<AddonRow> Addons { get; } = [];
    public ObservableCollection<AddonRow> Plugins { get; } = [];
    public ObservableCollection<AddonRow> PolPlugins { get; } = [];

    // top to bottom: this for the top of the page, then each list under its heading. Only rows come and go, so the
    // search box isn't rebuilt mid-typing
    public ObservableCollection<object> Items { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLists))] private string? _message = "";
    [ObservableProperty] private string _intro = "";
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _isWindower;
    [ObservableProperty] private bool _isAshita;
    [ObservableProperty] private bool _canChangeUseGlobal = true;
    [ObservableProperty] private string _useGlobalHelp = "";
    [ObservableProperty] private string? _noAddons;
    [ObservableProperty] private string? _noPlugins;
    [ObservableProperty] private string? _noPolPlugins;

    public bool HasLists => Message is null;

    partial void OnNoAddonsChanged(string? value) => _addonsHeading.Note = value;
    partial void OnNoPluginsChanged(string? value) => _pluginsHeading.Note = value;
    partial void OnNoPolPluginsChanged(string? value) => _polHeading.Note = value;

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
        try { page = Build(target, Lists(), _scan); }
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
        IsWindower = page.Message is null && page.Platform == ThirdPartyHookService.Windower;
        IsAshita = page.Message is null && page.Platform == ThirdPartyHookService.Ashita;
        _useGlobal = page.UseGlobal;
        OnPropertyChanged(nameof(UseGlobal));
        CanChangeUseGlobal = !(page.UseGlobal && page.ListLocked);
        var who = page.Key.Length == 0 ? "the default profile" : page.Key;
        UseGlobalHelp = page.UseGlobal
            ? $"This is the list in Windower's own settings, which Windower's launcher uses for every profile too. Turn this off to give {who} a list of its own."
            : $"{(page.Key.Length == 0 ? "The default profile" : page.Key)} has a list of its own. Turn this on to use the list in Windower's own settings again.";
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
        var next = data.Select(d => old.TryGetValue(d.Key, out var r) ? r.Update(d) : new AddonRow(this, d)).ToList();
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
        // hidden rows stay out of the list altogether, a collapsed one still costs the panel a container
        List<object> items = [this];
        if (HasLists)
        {
            items.Add(_addonsHeading);
            items.AddRange(Addons.Where(r => r.Visible));
            items.Add(_pluginsHeading);
            items.AddRange(Plugins.Where(r => r.Visible));
            if (IsAshita)
            {
                items.Add(_polHeading);
                items.AddRange(PolPlugins.Where(r => r.Visible));
            }
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
            if (_shown is { Platform: ThirdPartyHookService.Windower, Folder: { } dir } && _page is { } page)
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
            if (own is null) lists[key] = own = WindowerAddonList.Global(file);
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
        return file.LoadError is { } why ? throw Shown.Mark(new InvalidOperationException(Unreadable(why))) : file;
    }

    private static string Unreadable(string why) =>
        $"Windower's settings.xml couldn't be read ({why}), so its list can't be changed here. Fix the file, or choose Start a new settings file under Windower/Ashita to set it aside and start again.";

    internal void Switch(AddonRow row, bool on)
    {
        if (_shown is not { Folder: { } dir } target || _page is not { } page) return;
        ShowNote("");
        try
        {
            if (target.Platform == ThirdPartyHookService.Windower) SwitchWindower(dir, page.Key, row, on);
            else if (target.Platform == ThirdPartyHookService.Ashita) SwitchAshita(dir, target.Profile, row, on);
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
        Readable(dir);
        WindowerSettingsFile.Update(dir, f => Toggle(row.Kind == AddonKind.Addon ? f.Addons : f.Plugins, row.Key, on));
    }

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
                ? $"Provenance couldn't change {row.Name}, this boot configuration has no script set. Set one under Windower/Ashita."
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
        if (row is null || _shown is not { Platform: ThirdPartyHookService.Windower, Folder: { } dir } || _page is not { } page) return;
        row.Installing = true;
        ShowNote($"Installing {row.Name}...");
        try
        {
            var result = await _install(dir, page.Branch, row.Kind, row.Key);
            if (result.Installed) ShowNote($"{row.Name} is installed.");
            else ShowError($"{row.Name} couldn't be installed. {result.Errors.FirstOrDefault() ?? "Windower's servers didn't send it."}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not install {Name}", row.Key);
            ShowError(ErrorText.Of(ex, $"{row.Name} couldn't be installed."));
        }
        finally { row.Installing = false; }
        Refreshing = RefreshAsync();
        await Refreshing;
    }

    private static Task<WindowerInstallResult> InstallFromWindower(string dir, string branch, AddonKind kind, string name)
    {
        var updater = new WindowerUpdater(dir, branch);
        return kind == AddonKind.Plugin ? updater.InstallPluginAsync(name) : updater.InstallAddonAsync(name);
    }

    private static object? Scan(AddonsTarget target) => target switch
    {
        { Platform: ThirdPartyHookService.Windower, Folder: { } dir } when !ThirdPartyHookService.IsLinkedFolder(dir) =>
            new WindowerScan(WindowerPlugins.Scan(dir), WindowerAddons.Scan(dir), WindowerAddonTree.Load(WindowerAddonTree.CachePath(dir))),
        { Platform: ThirdPartyHookService.Ashita, Folder: { } dir } when !string.IsNullOrWhiteSpace(target.Profile) && File.Exists(BootPath(dir, target.Profile)) =>
            new AshitaScan(AshitaAddons.Scan(dir), AshitaPlugins.Scan(dir)),
        _ => null,
    };

    private static Page Build(AddonsTarget target, IReadOnlyDictionary<string, WindowerAddonList> lists, object? scan) => target switch
    {
        { Platform: ThirdPartyHookService.Windower, Folder: null } =>
            Page.Empty("Provenance couldn't find Windower. Choose its folder under Windower/Ashita first."),
        { Platform: ThirdPartyHookService.Windower, Folder: { } dir } when ThirdPartyHookService.IsLinkedFolder(dir) => Page.Empty(ThirdPartyHookService.LinkedWindowerText),
        { Platform: ThirdPartyHookService.Windower, Folder: { } dir } => BuildWindower(dir, target.Profile, lists, scan as WindowerScan),
        { Platform: ThirdPartyHookService.Ashita, Folder: null } =>
            Page.Empty("Provenance couldn't find Ashita. Choose its folder under Windower/Ashita first."),
        { Platform: ThirdPartyHookService.Ashita, Folder: { } dir } => BuildAshita(dir, target.Profile, scan as AshitaScan),
        _ => Page.Empty("Choose Windower or Ashita and its folder under Windower/Ashita first."),
    };

    private sealed record WindowerScan(IReadOnlyList<WindowerPluginInfo> Plugins, IReadOnlyList<WindowerAddonInfo> Addons, WindowerAddonTree? Tree);

    private static Page BuildWindower(string dir, string profileName, IReadOnlyDictionary<string, WindowerAddonList> lists, WindowerScan? scan)
    {
        scan ??= (WindowerScan)Scan(new AddonsTarget(ThirdPartyHookService.Windower, dir, profileName))!;
        var page = new Page { Platform = ThirdPartyHookService.Windower };
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
        if (file.LoadError is { } why)
        {
            locked = true;
            page.Problem = Unreadable(why);
        }
        var profile = file.Find(profileName);
        page.Key = profile?.Name ?? profileName;
        page.Branch = file.Branch;
        page.Intro = page.Key.Length == 0
            ? "Turn addons and plugins on or off for Windower's default profile. Changes save straight away."
            : $"Turn addons and plugins on or off for the Windower profile {page.Key}. Changes save straight away.";
        var effective = WindowerAddonList.Effective(file, lists, page.Key);
        page.UseGlobal = effective.UseGlobal;
        page.ListLocked = locked;
        profile ??= new WindowerProfile();
        bool frozen = locked && effective.UseGlobal;

        bool Listed(List<string> names, string name) => names.Contains(name, StringComparer.OrdinalIgnoreCase);
        var plugins = scan.Plugins.Where(p => !p.Required && (!p.Hidden || p.Installed || Listed(effective.Plugins, p.Name))).ToList();
        var missingPlugins = Missing(effective.Plugins, scan.Plugins.Select(p => p.Name));
        var pluginNames = plugins.Select(p => p.Name).Concat(missingPlugins).ToList();
        var installed = scan.Addons.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notInstalled = (scan.Tree?.Addons ?? []).Where(t => !installed.Contains(t.Name)).ToList();
        var missingAddons = Missing(effective.Addons, scan.Addons.Select(a => a.Name).Concat(notInstalled.Select(t => t.Name)));
        var addonNames = scan.Addons.Select(a => a.Name).Concat(notInstalled.Select(t => t.Name)).Concat(missingAddons).ToList();

        var loadedPlugins = Reasons(dir, profile, scan, false, pluginNames, []);
        var loadedAddons = Reasons(dir, profile, scan, false, [], addonNames);
        var globalPlugins = Reasons(dir, profile, scan, true, pluginNames, []);
        var globalAddons = Reasons(dir, profile, scan, true, [], addonNames);

        RowData Row(AddonKind kind, string key, string name, string? version, string? author, string? description, bool isInstalled, bool installable,
                    bool windowerPlugin = true)
        {
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
            var global = kind == AddonKind.Addon ? globalAddons : globalPlugins;
            if (effective.UseGlobal && isInstalled && windowerPlugin && global.TryGetValue(key, out var skip) && skip.EndsWith(OwnListOnly, StringComparison.Ordinal))
            {
                badges.Add(Badges.OwnListOnly);
                canTurnOn = false;
            }
            return new RowData(kind, key, name, version, author, description)
            {
                On = on, HasSwitch = hasSwitch, CanChange = !frozen && (canTurnOn || on), CanInstall = !isInstalled && installable, Badges = badges,
            };
        }

        page.Plugins = plugins.Select(p => Row(AddonKind.Plugin, p.Name, p.Name, (p.FileVersion ?? p.ManifestVersion)?.ToString(), null,
                                               Blank(p.Description), p.Installed, p.InManifest, p.IsWindowerPlugin))
            .Concat(missingPlugins.Select(n => Row(AddonKind.Plugin, n, n, null, null, null, false, false)))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        page.Addons = scan.Addons.Select(a => Row(AddonKind.Addon, a.Name, a.DisplayName, a.Version, a.Author, a.Description, true, false))
            .Concat(notInstalled.Select(t => Row(AddonKind.Addon, t.Name, t.Name, null, null, null, false, true)))
            .Concat(missingAddons.Select(n => Row(AddonKind.Addon, n, n, null, null, null, false, false)))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
    private static Dictionary<string, string> Reasons(string dir, WindowerProfile profile, WindowerScan scan, bool useGlobal,
                                                      List<string> plugins, List<string> addons)
    {
        var list = new WindowerAddonList { UseGlobal = useGlobal, Plugins = plugins, Addons = addons };
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in WindowerAddonList.ForLaunch(dir, profile, list, scan.Plugins, scan.Addons).Skipped)
        {
            int at = s.LastIndexOf(" (", StringComparison.Ordinal);
            if (at > 0 && s.EndsWith(')')) reasons.TryAdd(s[..at], s[(at + 2)..^1]);
        }
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
            Platform = ThirdPartyHookService.Ashita, Key = bootName,
            Intro = $"Turn addons and plugins on or off for the Ashita boot configuration {bootName}. Changes save straight away.",
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
            page.Problem = $"{bootName} is one of Ashita's example files, so Provenance won't change it. Duplicate it under Windower/Ashita and change the copy instead.";
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

        RowData Row(AddonKind kind, string key, string name, string? version, string? author, string? description, AshitaLoad state,
                    bool installed, AddonBadge? problem = null)
        {
            var badges = new List<AddonBadge>();
            if (!installed) badges.Add(Badges.NotInAshita);
            if (problem is not null) badges.Add(problem);
            else if (installed && !Loadable(kind, key)) badges.Add(problem = Badges.BadName);
            if (state == AshitaLoad.ByScript) badges.Add(Badges.ByScript);
            bool on = state == AshitaLoad.On, canTurnOn = installed && problem is null;
            return new RowData(kind, key, name, version, author, description)
            {
                On = on, HasSwitch = state != AshitaLoad.ByScript, CanChange = !readOnly && (canTurnOn || on), Badges = badges,
            };
        }

        List<RowData> Rows(AddonKind kind, IEnumerable<(string Key, string Name, string? Version, string? Author, string? Description, AddonBadge? Problem)> found,
                           IEnumerable<string> loaded, Func<string, AshitaLoad> state)
        {
            var list = found.ToList();
            return list.Select(f => Row(kind, f.Key, f.Name, f.Version, f.Author, f.Description, state(f.Key), true, f.Problem))
                .Concat(Missing(loaded, list.Select(f => f.Key)).Select(n => Row(kind, n, n, null, null, null, state(n), false)))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        page.Addons = Rows(AddonKind.Addon,
            scan.Addons.Select(a => (a.LoadName, a.Name, a.Version, a.Author, a.Description, a.NotForAshitaV4 ? Badges.NotForV4 : null)),
            script.Addons.Select(a => a.Name), script.Addon);
        page.Plugins = Rows(AddonKind.Plugin,
            scan.Plugins.Where(p => p.Kind == AshitaPluginKind.Plugin)
                .Select(p => (p.LoadName, p.Name, p.VersionText, p.Author, p.Description, p.ForOtherAshita ? Badges.OtherAshita : null)),
            script.Plugins.Select(p => p.Name), script.Plugin);

        var pol = boot.PolPlugins;
        AshitaLoad PolState(string n) => pol.Any(p => Same(p.Name, n) && p.On) ? AshitaLoad.On : AshitaLoad.Off;
        var polRows = new List<RowData>();
        foreach (var p in scan.Plugins.Where(p => p.Kind == AshitaPluginKind.PolPlugin))
        {
            if (AshitaBootFiles.IsTakeover(p.LoadName))
                polRows.Add(new RowData(AddonKind.PolPlugin, p.LoadName, p.LoadName, null, null, null)
                {
                    HasSwitch = false, CanChange = false, Badges = [Badges.NotWithProvenance],
                });
            else polRows.Add(Row(AddonKind.PolPlugin, p.LoadName, p.Name, p.VersionText, p.Author, p.Description, PolState(p.LoadName), true,
                                 p.ForOtherAshita ? Badges.OtherAshita : null));
        }
        var turnedOn = pol.Where(p => p.On && !AshitaBootFiles.IsTakeover(p.Name)).Select(p => p.Name);
        polRows.AddRange(Missing(turnedOn, scan.Plugins.Where(p => p.Kind == AshitaPluginKind.PolPlugin).Select(p => p.LoadName))
            .Select(n => Row(AddonKind.PolPlugin, n, n, null, null, null, AshitaLoad.On, false)));
        page.PolPlugins = polRows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        page.NoAddons = $"No addons found in {Path.Combine(dir, "addons")}.";
        page.NoPlugins = $"No plugins found in {Path.Combine(dir, "plugins")}.";
        page.NoPolPlugins = $"No PlayOnline plugins found in {Path.Combine(dir, "polplugins")}.";
        return page;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private sealed class Page
    {
        public string? Message;
        public string Intro = "";
        public string? Problem;
        public string Platform = ThirdPartyHookService.None;
        public bool UseGlobal = true, ListLocked;
        public string Key = "";
        public string Branch = "stable";
        public List<RowData> Addons = [], Plugins = [], PolPlugins = [];
        public string? NoAddons, NoPlugins, NoPolPlugins;

        public static Page Empty(string message) => new() { Message = message };
    }

    private static class Badges
    {
        public static readonly AddonBadge Install = new("Not installed", "Press Install to download it from Windower.");
        public static readonly AddonBadge NotInWindower = new("Not installed", "It's on the list, but it isn't in your Windower folder.");
        public static readonly AddonBadge NotInAshita = new("Not installed", "It's turned on, but it isn't in your Ashita folder.");
        public static readonly AddonBadge NotAPlugin = new("Not a Windower plugin",
            "This file in Windower's plugins folder isn't a Windower 4 plugin, so it can't be turned on.", Warn: true);
        public static readonly AddonBadge InitTxt = new("Loaded by your init.txt",
            "Windower's scripts\\init.txt loads this itself, so it loads no matter what the list says.");
        public static readonly AddonBadge InitCommand = new("Loaded by this profile's init command",
            "This profile's init command loads it itself, so it loads no matter what the list says.");
        public static readonly AddonBadge OwnListOnly = new("Only with this profile's own list",
            "Windower's shared list only takes addons and plugins that Windower offers itself. Turn off Use the same list for every profile to load this one.",
            Warn: true);
        public static readonly AddonBadge ByScript = new("Loaded by your script",
            "Your Ashita script loads this in a way Provenance can't safely change, so it's left as it is.");
        public static readonly AddonBadge NotForV4 = new("Not for Ashita v4",
            "This addon was written for Windower or an older Ashita, so it won't run in Ashita v4.", Warn: true);
        public static readonly AddonBadge OtherAshita = new("Built for a different Ashita",
            "This plugin was built for a different version of Ashita, so Ashita won't load it.", Warn: true);
        public static readonly AddonBadge BadName = new("Name Ashita can't load",
            "Ashita can't load a name like this one from a script or boot configuration. Rename it to plain letters and numbers to use it.", Warn: true);
        public static readonly AddonBadge NotWithProvenance = new("Not available with Provenance",
            "Provenance logs you in itself, so this is always off when Provenance starts Ashita.", Warn: true);
    }
}

public sealed partial class AddonsHeading(string? title, string? help = null) : ObservableObject
{
    public string? Title { get; } = title;
    public string? Help { get; } = help;
    [ObservableProperty] private string? _note;
}

internal sealed record RowData(AddonKind Kind, string Key, string Name, string? Version, string? Author, string? Description)
{
    public bool On { get; init; }
    public bool HasSwitch { get; init; } = true;
    public bool CanChange { get; init; } = true;
    public bool CanInstall { get; init; }
    public IReadOnlyList<AddonBadge> Badges { get; init; } = [];
}

public sealed partial class AddonRow : ObservableObject
{
    private readonly AddonsPluginsViewModel _owner;
    private bool _on;

    internal AddonRow(AddonsPluginsViewModel owner, RowData d)
    {
        _owner = owner;
        Kind = d.Kind;
        Key = d.Key;
        Update(d);
    }

    public AddonKind Kind { get; }
    public string Key { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _details;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private IReadOnlyList<AddonBadge> _badges = [];
    [ObservableProperty] private bool _hasSwitch;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SwitchEnabled))] private bool _canChange;
    [ObservableProperty] private bool _canInstall;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SwitchEnabled))] private bool _installing;
    [ObservableProperty] private bool _visible = true;
    private string? _author;

    public bool SwitchEnabled => CanChange && !Installing;

    // the page saves it and reads it all back, so a change that didn't save puts the switch back
    public bool On
    {
        get => _on;
        set
        {
            if (value == _on) return;
            if (HasSwitch && SwitchEnabled) _owner.Switch(this, value);
            OnPropertyChanged();
        }
    }

    internal AddonRow Update(RowData d)
    {
        Name = d.Name;
        _author = d.Author;
        Details = d.Version is { } v ? d.Author is { } a ? $"{v} by {a}" : v : d.Author is { } by ? "by " + by : null;
        Description = d.Description;
        if (!d.Badges.SequenceEqual(Badges)) Badges = d.Badges;
        HasSwitch = d.HasSwitch;
        CanChange = d.CanChange;
        CanInstall = d.CanInstall;
        if (_on != d.On)
        {
            _on = d.On;
            OnPropertyChanged(nameof(On));
        }
        return this;
    }

    internal bool Matches(string[] words) =>
        words.All(w => Name.Contains(w, StringComparison.OrdinalIgnoreCase) || Key.Contains(w, StringComparison.OrdinalIgnoreCase)
                       || _author?.Contains(w, StringComparison.OrdinalIgnoreCase) == true
                       || Description?.Contains(w, StringComparison.OrdinalIgnoreCase) == true);
}
