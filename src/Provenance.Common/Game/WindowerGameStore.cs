using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Provenance.Common.Security;

namespace Provenance.Common.Game;

public sealed class WindowerGameStore(string windowerDir, string profileName, RegistryGameStore registry, string? display = null) : IGameSettingsStore
{
    public string Key => $"windower|{Path.GetFullPath(windowerDir)}|{profileName}|{registry.Key}";
    public ThirdPartyHook? Platform => ThirdPartyHook.Windower;
    public string? ProfileName => profileName;
    public string? ProfileDisplay => display ?? (profileName.Length == 0 ? "Default" : profileName);
    public bool SameShapeRenderSizes => true;
    public bool ReadOnly => false;

    private WindowerProfile Profile(WindowerSettingsFile file) =>
        file.Find(profileName) ?? (profileName.Length == 0 ? file.Unnamed ?? new WindowerProfile() : null)
        ?? throw Shown.Mark(new InvalidOperationException($"The Windower profile {profileName} isn't in Windower's settings any more. Choose another one under Windower/Ashita."));

    public FfxiSettings Load()
    {
        if (FolderPins.Linked(windowerDir)) throw Shown.Mark(new IOException(WindowerInstall.LinkedText));
        if (LinkCheck.IsRedirect(WindowerSettingsFile.PathIn(windowerDir)))
            throw Shown.Mark(new IOException("Windower's settings.xml is a link to somewhere else, so Provenance won't change it."));
        var windows = registry.Load();
        var file = WindowerSettingsFile.Load(windowerDir);
        if (file.LoadError is not null)
            throw Shown.Mark(new InvalidDataException("Windower's settings.xml couldn't be read, so these settings can't be changed until it's fixed or a new one is started under Windower/Ashita."));
        return Read(Profile(file), windows);
    }

    public static FfxiSettings Read(WindowerProfile p, FfxiSettings windows)
    {
        var s = windows.Clone();
        foreach (var v in WindowerGameRegistry.Values(p, p.Width, p.Height))
            if (FfxiValues.Find(v.Name) is IntValue row)
                row.Set(s, v.Value is int i ? i : 0);
        s.PadMode = s.PadMode.Length >= 6 ? (int[])s.PadMode.Clone() : [.. s.PadMode, .. new int[6 - s.PadMode.Length]];
        s.PadMode[4] = p.AlwaysEnableGamepad ? 1 : 0;
        return s;
    }

    public GameSaveResult Save(FfxiSettings settings, FfxiSettings original)
    {
        var changed = GameFields.Changed(settings, original);
        var own = changed.Where(GameFields.Windower.Contains).ToList();
        if (own.Contains(GameFields.Resolution) && !own.Contains(GameFields.RenderSize)) own.Add(GameFields.RenderSize);
        if (own.Count > 0) WindowerSettingsFile.Update(windowerDir, f => Write(Profile(f), settings, own));
        var shared = changed.Where(f => !GameFields.Windower.Contains(f)).ToList();
        int written = 0;
        if (shared.Count > 0)
        {
            var windows = registry.LoadWindows();
            written = registry.Save(GameFields.With(windows, settings, shared), windows).Changed;
        }
        return new(own.Count + written, own.Count + written > 0);
    }

    public static void Write(WindowerProfile p, FfxiSettings s, IEnumerable<string> fields)
    {
        foreach (var f in fields)
            switch (f)
            {
                case GameFields.Resolution: (p.Width, p.Height) = (s.Width, s.Height); break;
                case GameFields.WindowMode:
                    p.WindowMode = s.WindowMode switch { 0 => WindowerWindowMode.Fullscreen, 1 => WindowerWindowMode.Window, _ => WindowerWindowMode.BorderlessWindow };
                    break;
                case GameFields.UiSize: p.UIScale = (float)FfxiSettings.MenuScale(s.Height, s.UiWidth, s.UiHeight); break;
                case GameFields.Brightness: p.Gamma = MathF.Round(s.Brightness, 1); break;
                case GameFields.RenderSize: p.SuperSampling = s.Width > 0 ? (float)s.BgWidth / s.Width : 1f; break;
                case GameFields.DistantTextures: p.MipMapLevel = Math.Max(0, s.DrawDistanceMip); break;
                case GameFields.EnhancedShading: p.BumpMapping = s.EnhancedShading == 1; break;
                case GameFields.Sound: p.Sound = s.Sound == 1; break;
                case GameFields.BackgroundSound: p.AlwaysPlaySound = s.BackgroundSound == 1; break;
                case GameFields.HardwareMouse: p.HardwareMouse = s.HardwareMouse == 1; break;
                case GameFields.PadBackground: p.AlwaysEnableGamepad = s.PadMode.Length > 4 && s.PadMode[4] == 1; break;
            }
    }
}
