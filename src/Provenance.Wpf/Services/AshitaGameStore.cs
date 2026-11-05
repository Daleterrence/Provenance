using System.IO;
using Provenance.Common;
using Provenance.Common.Game;
using Provenance.Common.Game.Ashita;

namespace Provenance.Wpf.Services;

// an Ashita boot configuration's [ffxi.registry], which Ashita hands the game in place of the registry's values
public sealed class AshitaGameStore(string ashitaDir, string bootName, RegistryGameStore registry, string? display = null) : IGameSettingsStore
{
    public string Key => $"ashita|{Path.GetFullPath(ashitaDir)}|{bootName}|{registry.Key}";
    public string? Platform => ThirdPartyHook.Ashita;
    public string? ProfileName => bootName;
    public string? ProfileDisplay => display ?? bootName;
    public bool SameShapeRenderSizes => false;
    public bool ReadOnly => AshitaBootFiles.IsShippedExample(bootName);

    private string PathOf()
    {
        if (!ThirdPartyHookLauncher.IsBootFileName(bootName))
            throw Shown.Mark(new InvalidOperationException("Choose an Ashita boot configuration under Windower/Ashita first."));
        var path = Path.Combine(AshitaBootFiles.Folder(ashitaDir), bootName);
        if (!File.Exists(path))
            throw Shown.Mark(new FileNotFoundException($"The Ashita boot configuration {bootName} isn't in Ashita's config\\boot folder any more. Choose another one under Windower/Ashita."));
        return path;
    }

    // what the file leaves to Windows shows the registry's value
    public FfxiSettings Load()
    {
        var windows = registry.Load();
        var cfg = AshitaBootConfig.Load(PathOf());
        return cfg.ReadGame(windows);
    }

    // every value goes in, so nothing's left to a registry the game may never see (sandbox empties it)
    public GameSaveResult Save(FfxiSettings settings, FfxiSettings original)
    {
        var changed = GameFields.Changed(settings, original);
        var path = PathOf();
        var cfg = AshitaBootConfig.Load(path);
        cfg.WriteGame(settings);
        bool wrote = cfg.Ini.Changed;
        if (wrote) cfg.Save(path);
        int shared = 0;
        var registryOnly = changed.Where(f => !GameFields.Ashita.Contains(f)).ToList();
        if (registryOnly.Count > 0)
        {
            var windows = registry.LoadWindows();
            shared = registry.Save(GameFields.With(windows, settings, registryOnly), windows).Changed;
        }
        return new(changed.Count, wrote || shared > 0);
    }
}
