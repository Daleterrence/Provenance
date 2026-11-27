using System.Diagnostics;

namespace Provenance.Common.Logging;

public static class GraphicsWrappers
{
    private static readonly string[] Names = ["d3d8.dll", "d3d9.dll", "d3d11.dll", "dxgi.dll", "dinput8.dll"];
    private static readonly string[] DgVoodooSettings = ["OutputAPI", "Adapters", "VRAM", "FullScreenMode"];

    public static IReadOnlyList<string> Lines(string? viewerDir, string? gameDir)
    {
        var lines = new List<string>();
        Add(lines, viewerDir, "beside pol.exe");
        if (!string.Equals(viewerDir, gameDir, StringComparison.OrdinalIgnoreCase)) Add(lines, gameDir, "in the game folder");
        return lines;
    }

    private static void Add(List<string> lines, string? dir, string where)
    {
        try
        {
            if (dir is null || !Directory.Exists(dir)) return;
            foreach (var name in Names)
                foreach (var file in Directory.GetFiles(dir, name))
                    lines.Add($"Graphics wrapper {where}: {Path.GetFileName(file)} ({Version(file)})");
            var conf = Path.Combine(dir, "dgVoodoo.conf");
            if (File.Exists(conf) && DgVoodoo(conf) is { } settings) lines.Add($"dgVoodoo.conf {where}: {settings}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string Version(string file)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(file);
            var text = $"{v.ProductName} {v.FileVersion}".Trim();
            return text.Length == 0 ? "no version details" : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "no version details"; }
    }

    private static string? DgVoodoo(string conf)
    {
        if (new FileInfo(conf).Length > 1024 * 1024) return null;
        var found = new List<string>();
        var lines = File.ReadAllLines(conf);
        foreach (var setting in DgVoodooSettings)
        {
            var line = lines.Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(setting, StringComparison.OrdinalIgnoreCase)
                                                                    && l[setting.Length..].TrimStart().StartsWith('='));
            if (line is not null) found.Add($"{setting} {line[(line.IndexOf('=') + 1)..].Trim()}");
        }
        return found.Count == 0 ? null : string.Join(", ", found);
    }
}
