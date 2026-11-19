using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Hook;
using Serilog;
using BootConfig = Provenance.Common.Game.Ashita.AshitaBootConfig;

namespace Provenance.Wpf.ViewModels.Settings;

public partial class AshitaCard : ObservableObject
{
    private const string NoScript = "None";

    private readonly IProfileHost _hook;
    private readonly CardWork _work = new();
    private bool _loadingCard;
    private int _loads;

    public AshitaCard(IProfileHost hook)
    {
        _hook = hook;
        hook.AshitaReload += Load;
    }

    internal event Action? Renamed;

    internal Task Pending { get; private set; } = Task.CompletedTask;

    internal CardWork Work => _work;

    public ObservableCollection<string> AshitaScripts { get; } = new();
    [ObservableProperty] private string _ashitaName = "";
    [ObservableProperty] private string? _ashitaScript;
    [ObservableProperty] private string _ashitaScriptArgs = "";
    [ObservableProperty] private bool _ashitaCardEnabled;
    [ObservableProperty] private string _ashitaCardNote = "";

    private string? AshitaFile => _hook.AshitaDir is { } dir && _hook.AshitaBootConfig is { } item ? Path.Combine(AshitaBootFiles.Folder(dir), item.Name) : null;

    private void Load()
    {
        int mine = ++_loads;
        _loadingCard = true;
        try
        {
            AshitaScripts.Clear();
            AshitaCardNote = "";
            AshitaCardEnabled = false;
            AshitaName = "";
            AshitaScript = null;
            AshitaScriptArgs = "";
        }
        finally { _loadingCard = false; }
        if (_hook.IsAshita && AshitaFile is { } path)
        {
            var dir = _hook.AshitaDir!;
            bool example = AshitaBootFiles.IsShippedExample(_hook.AshitaBootConfig!.Name);
            Pending = _work.Run(() => ReadBoot(dir, path), r => { if (mine == _loads) Show(r, example); });
        }
        _hook.NotifyProfilesChanged();
    }

    private sealed record BootRead(List<string> Scripts, BootConfig? Config, Exception? Failure);

    private static BootRead ReadBoot(string dir, string path)
    {
        var scripts = Scripts(dir).ToList();
        try { return new BootRead(scripts, BootConfig.Load(path), null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return new BootRead(scripts, null, ex); }
    }

    private void Show(BootRead r, bool example)
    {
        _loadingCard = true;
        try
        {
            AshitaScripts.Add(NoScript);
            foreach (var s in r.Scripts) AshitaScripts.Add(s);
            if (r.Config is not { } cfg)
            {
                Log.Warning(r.Failure, "Could not read the boot configuration");
                AshitaCardNote = "Provenance couldn't read this boot configuration.";
                return;
            }
            AshitaName = cfg.Name ?? "";
            var script = string.IsNullOrWhiteSpace(cfg.Script) ? NoScript : cfg.Script!;
            if (!AshitaScripts.Contains(script)) AshitaScripts.Add(script);
            AshitaScript = script;
            AshitaScriptArgs = cfg.ScriptArgs ?? "";
            AshitaCardEnabled = !example;
            if (example) AshitaCardNote = HookFailures.AshitaExampleCard;
        }
        finally { _loadingCard = false; }
        _hook.NotifyProfilesChanged();
    }

    private static IEnumerable<string> Scripts(string dir)
    {
        try
        {
            var folder = Path.Combine(dir, "scripts");
            return Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.txt").Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    partial void OnAshitaNameChanged(string value)
    {
        if (_loadingCard) return;
        SaveAshita(c => c.Name = value.Trim(), () =>
        {
            _hook.RefreshThirdPartyHookLists();
            Renamed?.Invoke();
        });
    }

    partial void OnAshitaScriptChanged(string? value) { if (!_loadingCard && value is not null) SaveAshita(c => c.Script = value == NoScript ? "" : value); }
    partial void OnAshitaScriptArgsChanged(string value) { if (!_loadingCard) SaveAshita(c => c.ScriptArgs = value.Trim()); }

    private void SaveAshita(Action<BootConfig> change, Action? saved = null)
    {
        if (!AshitaCardEnabled || AshitaFile is not { } path) return;
        Pending = _work.Run(() =>
        {
            try
            {
                var cfg = BootConfig.Load(path);
                change(cfg);
                if (cfg.Ini.Changed) cfg.Save(path);
                return null;
            }
            catch (Exception ex) { return ex; }
        }, ex =>
        {
            if (ex is null)
            {
                _hook.ProfileStatus.Clear();
                saved?.Invoke();
                return;
            }
            Log.Warning(ex, "Could not save the boot configuration");
            _hook.ProfileStatus.Fail(ErrorText.Of(ex, "Provenance couldn't save that to the boot configuration."));
            Load();
        });
    }
}
