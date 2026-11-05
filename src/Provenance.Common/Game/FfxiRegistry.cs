using System.Runtime.InteropServices;
using Microsoft.Win32;
using Provenance.Common.PolFormat;
using Provenance.Common.Win32;

namespace Provenance.Common.Game;

// HKLM, so writes need admin. 0001/0002, 0003/0004 and 0037/0038 always go as pairs, 0028 is float bits
public static class FfxiRegistry
{
    public static string KeyPath(PolRegion region) => region switch
    {
        PolRegion.Japan => @"SOFTWARE\PlayOnline\SQUARE\FinalFantasyXI",
        PolRegion.Europe => @"SOFTWARE\PlayOnlineEU\SquareEnix\FinalFantasyXI",
        _ => @"SOFTWARE\PlayOnlineUS\SquareEnix\FinalFantasyXI",
    };

    public static RegistryKey? Open(PolRegion region, bool write)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        return write ? hklm.CreateSubKey(KeyPath(region), writable: true) : hklm.OpenSubKey(KeyPath(region));
    }

    private static RegistryKey OpenForWrite(PolRegion region) =>
        Open(region, true) ?? throw Shown.Mark(new InvalidOperationException("Provenance couldn't open the FINAL FANTASY XI settings."));

    public static FfxiSettings LoadFrom(RegistryKey k)
    {
        var d = FfxiSettings.Defaults();
        int I(string n, int def) => k.GetValue(n) is int v ? v : def;
        var s = new FfxiSettings
        {
            DrawDistanceMip = I("0000", d.DrawDistanceMip), Width = I("0001", d.Width), Height = I("0002", d.Height),
            BgWidth = I("0003", d.BgWidth), BgHeight = I("0004", d.BgHeight), Sound = I("0007", d.Sound),
            Vegetation = I("0011", d.Vegetation), EnhancedShading = I("0017", d.EnhancedShading),
            Textures = I("0018", d.Textures), Maps2D = I("0019", d.Maps2D), HardwareMouse = I("0021", d.HardwareMouse),
            OpeningMovie = I("0022", d.OpeningMovie), SimpleCharSelect = I("0023", d.SimpleCharSelect),
            Brightness = k.GetValue("0028") is int b ? BitConverter.Int32BitsToSingle(b) : d.Brightness,
            SoundEffects = I("0029", d.SoundEffects), WindowMode = I("0034", d.WindowMode),
            BackgroundSound = I("0035", d.BackgroundSound), Fonts = I("0036", d.Fonts), UiWidth = I("0037", d.UiWidth),
            UiHeight = I("0038", d.UiHeight), GraphicsFix = I("0040", d.GraphicsFix),
            ScreenshotFolder = k.GetValue("0042") as string ?? "",
            FullResScreenshots = I("0043", d.FullResScreenshots), KeepAspect = I("0044", d.KeepAspect),
            LegacySettings = I("0045", d.LegacySettings),
        };
        if (k.GetValue("padmode000") is string pm)
        {
            var parts = pm.Split(',');
            for (int i = 0; i < 6 && i < parts.Length; i++)
                s.PadMode[i] = int.TryParse(parts[i].Trim(), out var v) ? v : d.PadMode[i];
        }
        else s.PadMode = d.PadMode;
        return s;
    }

    public static bool NeedsFirstStart(PolRegion region)
    {
        using var k = Open(region, false);
        return k == null || new[] { "0001", "0002", "0003", "0004", "0034", "0037", "0038" }.Any(n => k.GetValue(n) is not int);
    }

    // only what's missing, like the official tool does when it opens
    public static int WriteFirstStart(PolRegion region)
    {
        var (w, h) = PrimaryDisplay();
        using var k = OpenForWrite(region);
        return FillMissing(k, FfxiSettings.FirstStart(w, h));
    }

    // 0020, 0024, 0031-0033 and 0039 have no UI, 0030 (3D LCD) and 0041 are legacy-tool only. All get the tool's
    // defaults (0031 is 0.006f)
    public static int FillMissing(RegistryKey k, FfxiSettings s)
    {
        int n = 0;
        // the installer writes 0034 = 1 (windowed) itself, so it gets set too while the resolution never has been
        if (k.GetValue("0001") is null && k.GetValue("0034") is int mode && mode != s.WindowMode)
        {
            k.SetValue("0034", s.WindowMode, RegistryValueKind.DWord);
            n++;
        }
        bool Missing(string name) => k.GetValue(name) is null;
        void Set(string name, object v, RegistryValueKind kind = RegistryValueKind.DWord)
        {
            k.SetValue(name, v, kind);
            n++;
        }
        void D(string name, int v) { if (Missing(name)) Set(name, v); }
        void Pair(string a, string b, int va, int vb)
        {
            if (!Missing(a) && !Missing(b)) return;
            Set(a, va);
            Set(b, vb);
        }
        D("0000", s.DrawDistanceMip);
        Pair("0001", "0002", s.Width, s.Height);
        Pair("0003", "0004", s.BgWidth, s.BgHeight);
        D("0007", s.Sound);
        D("0011", s.Vegetation);
        D("0017", s.EnhancedShading);
        D("0018", s.Textures);
        D("0019", s.Maps2D);
        D("0020", 1);
        D("0021", s.HardwareMouse);
        D("0022", s.OpeningMovie);
        D("0023", s.SimpleCharSelect);
        D("0024", 0);
        D("0028", BitConverter.SingleToInt32Bits(s.Brightness));
        D("0029", s.SoundEffects);
        D("0030", 0);
        D("0031", 0x3BC49BA6);
        D("0032", 0);
        D("0033", 0);
        D("0034", s.WindowMode);
        D("0035", s.BackgroundSound);
        D("0036", s.Fonts);
        Pair("0037", "0038", s.UiWidth, s.UiHeight);
        D("0039", 1);
        D("0040", s.GraphicsFix);
        D("0041", 0);
        if (Missing("0042")) Set("0042", s.ScreenshotFolder, RegistryValueKind.String);
        D("0043", s.FullResScreenshots);
        D("0044", s.KeepAspect);
        D("0045", s.LegacySettings);
        if (Missing("padmode000")) Set("padmode000", string.Join(",", s.PadMode), RegistryValueKind.String);
        return n;
    }

    // real pixels, not scaled by Windows' text size
    public static (int Width, int Height) PrimaryDisplay()
    {
        const int ENUM_CURRENT_SETTINGS = -1;
        return DisplayMode(ENUM_CURRENT_SETTINGS) is { Width: > 0, Height: > 0 } m ? m : (1920, 1080);
    }

    public static IEnumerable<(int Width, int Height)> PrimaryDisplayModes()
    {
        for (int i = 0; DisplayMode(i) is { } m; i++) yield return m;
    }

    private static (int Width, int Height)? DisplayMode(int modeNum)
    {
        var dm = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
        return User32.EnumDisplaySettingsW(null, modeNum, ref dm) ? (dm.dmPelsWidth, dm.dmPelsHeight) : null;
    }

    public static int SaveTo(RegistryKey k, FfxiSettings s, FfxiSettings original)
    {
        int n = 0;
        void Set(string name, object v, RegistryValueKind kind = RegistryValueKind.DWord)
        {
            k.SetValue(name, v, kind);
            n++;
        }
        void D(string name, int v, int was) { if (v != was) Set(name, v); }
        void Pair(string a, string b, int va, int vb, int wa, int wb)
        {
            if (va == wa && vb == wb) return;
            Set(a, va);
            Set(b, vb);
        }
        D("0000", s.DrawDistanceMip, original.DrawDistanceMip);
        Pair("0001", "0002", s.Width, s.Height, original.Width, original.Height);
        Pair("0003", "0004", s.BgWidth, s.BgHeight, original.BgWidth, original.BgHeight);
        D("0007", s.Sound, original.Sound);
        D("0011", Math.Clamp(s.Vegetation, 0, 2), original.Vegetation);
        D("0017", s.EnhancedShading, original.EnhancedShading);
        D("0018", Math.Clamp(s.Textures, 0, 2), original.Textures);
        D("0019", Math.Clamp(s.Maps2D, 0, 1), original.Maps2D);
        D("0021", s.HardwareMouse, original.HardwareMouse);
        D("0022", s.OpeningMovie, original.OpeningMovie);
        D("0023", s.SimpleCharSelect, original.SimpleCharSelect);
        var bright = MathF.Round(Math.Clamp(s.Brightness, -1f, 1f), 1);
        // a value from elsewhere between two tenths stays as it is till it's moved
        if (bright != MathF.Round(Math.Clamp(original.Brightness, -1f, 1f), 1)) Set("0028", BitConverter.SingleToInt32Bits(bright));
        D("0029", Math.Clamp(s.SoundEffects, 12, 20), original.SoundEffects);
        D("0034", s.WindowMode is 0 or 1 or 3 ? s.WindowMode : 1, original.WindowMode);
        D("0035", s.BackgroundSound, original.BackgroundSound);
        D("0036", Math.Clamp(s.Fonts, 0, 2), original.Fonts);
        Pair("0037", "0038", s.UiWidth, s.UiHeight, original.UiWidth, original.UiHeight);
        D("0040", s.GraphicsFix, original.GraphicsFix);
        if (s.ScreenshotFolder != original.ScreenshotFolder) Set("0042", s.ScreenshotFolder, RegistryValueKind.String);
        D("0043", s.FullResScreenshots, original.FullResScreenshots);
        D("0044", s.KeepAspect, original.KeepAspect);
        D("0045", s.LegacySettings, original.LegacySettings);
        if (!s.PadMode.SequenceEqual(original.PadMode))
            Set("padmode000", string.Join(",", s.PadMode), RegistryValueKind.String);
        return n;
    }
}
