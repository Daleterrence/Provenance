using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Provenance.Common.Settings;
using Serilog;

namespace Provenance.Wpf.Services;

// the app's two looks. The views use DynamicResource for every colour, so swapping the palette restyles open windows
internal static class AppTheme
{
    public const string Dark = "dark";
    public const string Light = "light";

    public static readonly string[] Names = [Dark, Light];

    public static string Current =>
        Application.Current?.Resources.MergedDictionaries.Select(PaletteName).LastOrDefault(n => n is not null) ?? Dark;

    public static event Action? Changed;

    public static string Normalise(string? name) =>
        string.Equals(name?.Trim(), Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;

    public static void Apply(string? name)
    {
        var theme = Normalise(name);
        var app = Application.Current;
        if (app is null) return;
        var merged = app.Resources.MergedDictionaries;
        int at = -1;
        for (int i = 0; i < merged.Count; i++)
            if (PaletteName(merged[i]) is not null) at = i;
        var before = Current;
        var palette = at >= 0 ? merged[at] : null;
        if (palette is null || PaletteName(palette) != theme)
        {
            palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Provenance;component/Themes/{Title(theme)}.xaml") };
            if (at >= 0) merged[at] = palette;
            else merged.Add(palette);
        }

        // MaterialDesign's own controls (fields, switches, checks, menus, scrollbars) follow along. Done even when the
        // palette's already in, as MaterialDesign starts out on its own colours
        try
        {
            var helper = new PaletteHelper();
            var md = helper.GetTheme();
            md.SetBaseTheme(theme == Light ? BaseTheme.Light : BaseTheme.Dark);
            if (palette["Theme.MaterialPrimary"] is Color primary)
            {
                md.SetPrimaryColor(primary);
                md.SetSecondaryColor(primary);
            }
            helper.SetTheme(md);
        }
        catch (Exception ex) { Log.Warning(ex, "Couldn't switch MaterialDesign to the {Theme} theme", theme); }

        if (theme != before) Changed?.Invoke();
    }

    public static string FromSettingsFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return Dark;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(nameof(LauncherSettings.Theme), out var t) && t.ValueKind == JsonValueKind.String
                ? Normalise(t.GetString()) : Dark;
        }
        catch { return Dark; }
    }

    private static string Title(string theme) => theme == Light ? "Light" : "Dark";

    private static string? PaletteName(ResourceDictionary d)
    {
        var source = d.Source?.OriginalString ?? "";
        if (source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)) return Dark;
        if (source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)) return Light;
        return null;
    }
}
