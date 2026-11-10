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
    private bool _loadingCard;

    public AshitaCard(IProfileHost hook)
    {
        _hook = hook;
        hook.AshitaReload += Load;
    }

    // the boot configuration took a new name, so the lists, the accounts and the Game page show it
    internal event Action? Renamed;

    public ObservableCollection<string> AshitaScripts { get; } = new();
    [ObservableProperty] private string _ashitaName = "";
    [ObservableProperty] private string? _ashitaScript;
    [ObservableProperty] private string _ashitaScriptArgs = "";
    [ObservableProperty] private bool _ashitaCardEnabled;
    [ObservableProperty] private string _ashitaCardNote = "";

    private string? AshitaFile => _hook.AshitaDir is { } dir && _hook.AshitaBootConfig is { } item ? Path.Combine(AshitaBootFiles.Folder(dir), item.Name) : null;

    private void Load()
    {
        _loadingCard = true;
        try
        {
            AshitaScripts.Clear();
            AshitaCardNote = "";
            AshitaCardEnabled = false;
            AshitaName = "";
            AshitaScript = null;
            AshitaScriptArgs = "";
            if (!_hook.IsAshita || AshitaFile is not { } path) return;
            AshitaScripts.Add(NoScript);
            foreach (var s in Scripts(_hook.AshitaDir!)) AshitaScripts.Add(s);
            BootConfig cfg;
            try { cfg = BootConfig.Load(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Log.Warning(ex, "Could not read the boot configuration");
                AshitaCardNote = "Provenance couldn't read this boot configuration.";
                return;
            }
            AshitaName = cfg.Name ?? "";
            var script = string.IsNullOrWhiteSpace(cfg.Script) ? NoScript : cfg.Script!;
            if (!AshitaScripts.Contains(script)) AshitaScripts.Add(script);
            AshitaScript = script;
            AshitaScriptArgs = cfg.ScriptArgs ?? "";
            bool example = AshitaBootFiles.IsShippedExample(_hook.AshitaBootConfig!.Name);
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
        if (_loadingCard || !SaveAshita(c => c.Name = value.Trim())) return;
        _hook.RefreshThirdPartyHookLists();
        Renamed?.Invoke();
    }

    partial void OnAshitaScriptChanged(string? value) { if (!_loadingCard && value is not null) SaveAshita(c => c.Script = value == NoScript ? "" : value); }
    partial void OnAshitaScriptArgsChanged(string value) { if (!_loadingCard) SaveAshita(c => c.ScriptArgs = value.Trim()); }

    private bool SaveAshita(Action<BootConfig> change)
    {
        if (!AshitaCardEnabled || AshitaFile is not { } path) return false;
        try
        {
            var cfg = BootConfig.Load(path);
            change(cfg);
            if (cfg.Ini.Changed) cfg.Save(path);
            _hook.ProfileStatus.Clear();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save the boot configuration");
            _hook.ProfileStatus.Fail(ErrorText.Of(ex, "Provenance couldn't save that to the boot configuration."));
            Load();
            return false;
        }
    }
}
