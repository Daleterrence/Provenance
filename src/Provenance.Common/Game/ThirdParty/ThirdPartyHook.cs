using System.Text.Json;
using System.Text.Json.Serialization;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Game.ThirdParty;

// the numbers are the order of the choices on the Windower/Ashita page. settings.json keeps the lower case words
[JsonConverter(typeof(ThirdPartyHookJson))]
public enum ThirdPartyHook { None = 0, Ashita = 1, Windower = 2 }

// reads whatever case and spacing an older build wrote, and anything it doesn't know as None
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
        foreach (var pf in new[] { AppPaths.ProgramFilesX86, AppPaths.ProgramFiles })
            foreach (var n in names)
                if (!string.IsNullOrEmpty(pf)) yield return Path.Combine(pf, n);
    }

    internal static PolRegion? RegionOfPol(string polExe) =>
        Path.GetDirectoryName(polExe) is { Length: > 0 } dir ? PolEnv.Region(dir) : null;
}
