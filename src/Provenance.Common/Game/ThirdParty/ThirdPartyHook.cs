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
    // a drive the player mapped in their own session usually isn't there for this elevated process, so a persistent
    // network mapping becomes its share's path. Everything that reads or starts the folder has to use what this gives back
    public static string? Usable(string? dir) => dir is null ? null : DriveTargets.Resolve(dir);

    // only the drive letter, nothing that could wait on a share, so a settings page can ask it
    public static string? DriveProblem(string? dir) =>
        Usable(dir) is { } real && FolderPins.DriveMissing(real)
            ? $"Drive {char.ToUpperInvariant(real[0])}: isn't available to Provenance. If it's a SUBST drive, choose the folder by its real path instead."
            : null;

    public static void RequireDrive(string? dir)
    {
        if (Usable(dir) is not { } real) return;
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
