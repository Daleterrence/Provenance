using Provenance.Common.PolFormat;
using Serilog;

namespace Provenance.Common.Game;

public sealed class FfxiSettings
{
    public int DrawDistanceMip;        // 0 = Draw Distance on (full textures far away), 1 = MIP mapping
    public int Width, Height;
    public int BgWidth, BgHeight;      // 3D render resolution
    public int Sound;
    public int Vegetation;             // 0 off, 1 normal, 2 smooth
    public int EnhancedShading;
    public int Textures;               // 0 standard, 1 low compression, 2 high quality
    public int Maps2D;                 // 0 compressed, 1 high quality
    public int HardwareMouse;
    public int OpeningMovie;
    public int SimpleCharSelect;
    public float Brightness;           // -1.0 .. 1.0
    public int SoundEffects;           // 12 .. 20
    public int WindowMode;             // 0 fullscreen, 1 windowed, 3 borderless
    public int BackgroundSound;
    public int Fonts;                  // 0 compressed, 1 uncompressed, 2 high quality
    public int UiWidth, UiHeight;      // 0/0 = same as the window
    public int GraphicsFix;
    public string ScreenshotFolder = "";
    public int FullResScreenshots;
    public int KeepAspect;
    public int LegacySettings;
    public int[] PadMode = new int[6]; // padmode000: enable, force feedback, slider, hat, background, XInput

    public static int KnownWindowMode(int v) => v is 0 or 1 or 3 ? v : 0;

    public FfxiSettings Clone()
    {
        var c = (FfxiSettings)MemberwiseClone();
        c.PadMode = (int[])PadMode.Clone();
        return c;
    }

    public static FfxiSettings Defaults() => new()
    {
        DrawDistanceMip = 0, Width = 800, Height = 600, BgWidth = 800, BgHeight = 600, Sound = 1,
        Vegetation = 2, EnhancedShading = 1, Textures = 2, Maps2D = 1, HardwareMouse = 1, OpeningMovie = 1,
        SimpleCharSelect = 0, Brightness = 0f, SoundEffects = 12, WindowMode = 1, BackgroundSound = 0,
        Fonts = 2, UiWidth = 0, UiHeight = 0, GraphicsFix = 0, ScreenshotFolder = "", FullResScreenshots = 1,
        KeepAspect = 1, LegacySettings = 0, PadMode = [0, 1, 1, 1, 0, 0],
    };

    public static FfxiSettings FirstStart(int screenWidth, int screenHeight)
    {
        var s = Defaults();
        s.WindowMode = 0;
        s.Width = screenWidth;
        s.Height = screenHeight;
        s.BgWidth = s.BgHeight = RenderSizes.ClosestTo(screenWidth);
        s.KeepAspect = 0;
        (s.UiWidth, s.UiHeight) = MenuResolution(screenWidth, screenHeight, Math.Min(1.5, MaxMenuScale(screenWidth, screenHeight)));
        s.Sound = 1;
        s.SoundEffects = 20;
        s.ScreenshotFolder = Path.Combine(AppPaths.Documents, "My Games", "FINAL FANTASY XI");
        return s;
    }

    public static void WriteFirstStartSettings(PolRegion region)
    {
        try
        {
            if (!FfxiRegistry.NeedsFirstStart(region)) return;
            int n = FfxiRegistry.WriteFirstStart(region);
            var d = Display.Primary();
            Log.Information("FINAL FANTASY XI first-start settings: {Count} values written for a {W}x{H} display", n, d.Width, d.Height);
        }
        catch (Exception e) { Log.Warning(e, "Could not write the FINAL FANTASY XI first-start settings"); }
    }

    public static double SnapMenuScale(double scale) => Math.Round(scale * 20) / 20;

    public static (int Width, int Height) MenuResolution(int width, int height, double scale) =>
        scale <= 1 ? (0, 0) : ((int)Math.Round(width / scale), (int)Math.Round(height / scale));

    public static double MenuScale(int height, int uiWidth, int uiHeight) =>
        uiWidth <= 0 || uiHeight <= 0 ? 1 : SnapMenuScale((double)height / uiHeight);

    // the official tool's list stops before the menus drop under 512 high. Width counts too here, for portrait screens
    public static double MaxMenuScale(int width, int height) => Math.Max(1, Math.Min(width, height) * 20 / 512 / 20.0);
}
