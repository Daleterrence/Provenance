using System.Reflection;
using Velopack;

namespace Provenance.Wpf.Services;

// The tag after the version in the main window's title. It only ever comes from the update server's feed, never from
// anything saved on this PC
internal static class VersionTag
{
    public const string PreRelease = "[PRE-RELEASE VERSION, POSSIBLY UNSTABLE]";
    public const string UpdateAvailable = "[UPDATE AVAILABLE]";
    public const string Outdated = "[OUTDATED & UNSUPPORTED]";

    // this many releases newer than a copy and it's still just an update
    public const int MostBehind = 3;

    // null (no tag) for the same as the newest release, and for a feed with nothing in it. The feed's pruned, so
    // this only holds while it keeps at least the four newest releases
    public static string? For(SemanticVersion current, IEnumerable<SemanticVersion> feed)
    {
        var releases = feed.Distinct().ToList();
        if (releases.Count == 0) return null;
        if (current > releases.Max()!) return PreRelease;
        int behind = releases.Count(r => r > current);
        return behind == 0 ? null : behind <= MostBehind ? UpdateAvailable : Outdated;
    }

    // what this build calls itself, without the commit on the end
    public static string? OwnText() =>
        typeof(VersionTag).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] is { Length: > 0 } text
            ? text
            : null;

    public static SemanticVersion? Own() => OwnText() is { } text && SemanticVersion.TryParse(text, out var version) ? version : null;

    public static string WindowTitle(string? version, string? tag) => Join("Provenance", version, tag);

    public static string TitleBarText(string? version, string? tag) => Join("PROVENANCE", version, tag);

    private static string Join(string name, string? version, string? tag) =>
        string.Join(' ', new[] { name, version, tag }.Where(s => !string.IsNullOrEmpty(s)));
}
