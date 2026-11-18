using System.Text.Json;
using System.Text.Json.Serialization;
using Provenance.Common.PolFormat;
using Provenance.Common.Security;
using Provenance.Common.Win32;

namespace Provenance.Common.Game.ThirdParty;

// the numbers are the order of the choices on the Windower/Ashita page. settings.json keeps the lower case words
[JsonConverter(typeof(ThirdPartyHookJson))]
public enum ThirdPartyHook { None = 0, Ashita = 1, Windower = 2 }

// reads any case and spacing, and anything it doesn't know as None
public sealed class ThirdPartyHookJson : JsonConverter<ThirdPartyHook>
{
    public override bool HandleNull => true;

    public override ThirdPartyHook Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => ThirdPartyHook.None,
            JsonTokenType.String => ThirdPartyHooks.Parse(reader.GetString()),
            _ => throw new JsonException(),
        };

    public override void Write(Utf8JsonWriter writer, ThirdPartyHook value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ThirdPartyHooks.Word(value));
}

public static class ThirdPartyHooks
{
    // mapped drives usually aren't there for an elevated process, so a persistent mapping becomes its share's path and
    // callers have to use what comes back. A share that doesn't answer in time isn't usable
    public static string? Usable(string? dir)
    {
        if (dir is null) return null;
        var resolved = DriveTargets.Resolve(dir);
        return ShareRoot(resolved) is { } root && !ShareAnswers(root) ? null : resolved;
    }

    internal static TimeSpan ShareWait = TimeSpan.FromSeconds(1.5), ShareKeep = TimeSpan.FromSeconds(10);
    internal static Func<string, bool> ShareExists = Directory.Exists;
    private static readonly Dictionary<string, (bool Up, DateTime At)> Shares = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Asking = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ShareLock = new();

    internal static void ForgetShares()
    {
        lock (ShareLock) Shares.Clear();
    }

    // up or down is kept for a few seconds, and a check that's still out counts as down without another being started
    private static bool ShareAnswers(string root)
    {
        Task<bool> ask;
        lock (ShareLock)
        {
            if (Shares.TryGetValue(root, out var known) && DateTime.UtcNow - known.At < ShareKeep) return known.Up;
            if (Asking.Contains(root)) return false;
            Asking.Add(root);
            ask = Task.Run(() =>
            {
                bool up;
                try { up = ShareExists(root); }
                catch { up = false; }
                lock (ShareLock)
                {
                    Shares[root] = (up, DateTime.UtcNow);
                    Asking.Remove(root);
                }
                return up;
            });
        }
        if (ask.Wait(ShareWait)) return ask.Result;
        lock (ShareLock)
            if (Asking.Contains(root)) Shares[root] = (false, DateTime.UtcNow);
        return false;
    }

    // the share a path is on, a local one gives null without anything being asked
    internal static string? ShareRoot(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return null;
        }
        else if (DriveTargets.HasDriveLetter(path) && DriveTargets.Remote(path[0])) path = DriveTargets.Real(path);
        else return null;
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var parts = path[2..].Split('\\', 3);
        if (parts[0].Length == 0) return null;
        return parts.Length > 1 && parts[1].Length > 0 ? @"\\" + parts[0] + '\\' + parts[1] : @"\\" + parts[0];
    }

    // only the drive letter, nothing that could wait on a share, so a settings page can ask it
    public static string? DriveProblem(string? dir) =>
        dir is not null && DriveTargets.Resolve(dir) is var real && FolderPins.DriveMissing(real)
            ? $"Drive {char.ToUpperInvariant(real[0])}: isn't available to Provenance. If it's a SUBST drive, choose the folder by its real path instead."
            : null;

    public static void RequireDrive(string? dir)
    {
        if (dir is null || DriveTargets.Resolve(dir) is not { } real) return;
        if (DriveProblem(real) is { } missing) throw Shown.Mark(new IOException(missing));
        if (real.StartsWith(@"\\", StringComparison.Ordinal) && !Directory.Exists(real))
            throw Shown.Mark(new IOException($"Provenance can't open {real}, so it can't use the folder. Check the share is reachable and that this Windows account can open it, then try again."));
    }

    public static ThirdPartyHook Parse(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "ashita" => ThirdPartyHook.Ashita,
        "windower" => ThirdPartyHook.Windower,
        _ => ThirdPartyHook.None,
    };

    public static string Word(ThirdPartyHook hook) => hook switch
    {
        ThirdPartyHook.Ashita => "ashita",
        ThirdPartyHook.Windower => "windower",
        _ => "none",
    };

    internal static IEnumerable<string> Candidates(string? configured, string root, params string[] names)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured!;
        yield return root;
        foreach (var pf in ThirdPartyHookFinder.ProgramFilesRoots())
            foreach (var n in names)
                yield return Path.Combine(pf, n);
    }

    internal static PolRegion? RegionOfPol(string polExe) =>
        Path.GetDirectoryName(polExe) is { Length: > 0 } dir ? PolEnv.Region(dir) : null;
}
