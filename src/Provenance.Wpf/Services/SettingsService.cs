using System.IO;
using System.Text.Json;
using Serilog;
using Provenance.Common.Locks;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private bool _saveFailedRaised;
    private PolRegion? _region;

    public LauncherSettings Current { get; private set; }

    public event Action<string>? SaveFailed;

    public string? LoadProblem { get; private set; }

    // looked up again until it's found, so an install made while the app runs gets picked up
    public PolRegion? DetectedRegion => _region ??= PolPaths.TryAutoDetectRegion();

    public SettingsService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Provenance", "settings.json")) { }

    internal SettingsService(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _path = path;
        Current = Load();
    }

    private LauncherSettings Load()
    {
        if (!File.Exists(_path))
        {
            Log.Information("No settings.json, using defaults");
            return new LauncherSettings();
        }
        try
        {
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(_path)) ?? new LauncherSettings();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not parse settings.json, falling back to defaults");
            // the next save replaces settings.json, account list and all
            var bad = _path + ".bad";
            bool kept;
            try { File.Copy(_path, bad, overwrite: true); kept = true; } catch { kept = false; }
            LoadProblem = "Provenance couldn't read your settings file, so it started with the default settings. "
                        + (kept ? $"There's a copy of the old file at {bad}." : "Provenance couldn't keep a copy of it.");
            return new LauncherSettings();
        }
    }

    // temp file then a move, a truncated settings.json would load as defaults and lose every account
    public void Save(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Current = settings;
        var tmp = $"{_path}.{Environment.ProcessId}.tmp";
        IDisposable? gate = null;
        try
        {
            try { gate = AdminLock.TryAcquire(AdminLock.PerUser("settings"), TimeSpan.FromSeconds(3)); }
            catch (Exception ex) { Log.Warning(ex, "Could not take the settings lock; saving without it"); }
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
            Log.Debug("Settings saved to {Path}", _path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save settings.json");
            try { File.Delete(tmp); } catch { }
            if (!_saveFailedRaised)
            {
                _saveFailedRaised = true;
                SaveFailed?.Invoke(ex.Message);
            }
        }
        finally { gate?.Dispose(); }
    }

    // Save's move fails while anything has the file open, whatever it shares, so this waits its turn at the same lock
    public LauncherSettings? ReadSaved()
    {
        try
        {
            using var gate = AdminLock.TryAcquire(AdminLock.PerUser("settings"), TimeSpan.FromMilliseconds(500));
            if (gate is null || !File.Exists(_path)) return null;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<LauncherSettings>(fs);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not read settings.json again");
            return null;
        }
    }
}
