using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;

namespace Provenance.Common.Game;

public sealed class AshitaGameStore(string ashitaDir, string bootName, RegistryGameStore registry, string? display = null) : IGameSettingsStore
{
    public string Key => $"ashita|{Path.GetFullPath(ashitaDir)}|{bootName}|{registry.Key}";
    public ThirdPartyHook? Platform => ThirdPartyHook.Ashita;
    public string? ProfileName => bootName;
    public string? ProfileDisplay => display ?? bootName;
    public bool SameShapeRenderSizes => false;
    public bool ReadOnly => AshitaBootFiles.IsShippedExample(bootName);

    private string PathOf()
    {
        if (!AshitaBootFiles.IsBootFileName(bootName))
            throw Shown.Mark(new InvalidOperationException("Choose an Ashita boot configuration under Windower/Ashita first."));
        var path = Path.Combine(AshitaBootFiles.Folder(ashitaDir), bootName);
        if (!File.Exists(path))
            throw Shown.Mark(new FileNotFoundException($"The Ashita boot configuration {bootName} isn't in Ashita's config\\boot folder any more. Choose another one under Windower/Ashita."));
        return path;
    }

    public FfxiSettings Load()
    {
        var windows = registry.Load();
        var cfg = AshitaBootConfig.Load(PathOf());
        return cfg.ReadGame(windows);
    }

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
