using System.Text.Json;

namespace Provenance.Common;

// forgiving reads for the hook's snapshot and the feed worker's JSON, a missing field gets the fallback
internal static class JsonFields
{
    public static string Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static bool Bool(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    public static uint UInt(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.TryGetUInt32(out var u) ? u : 0;

    public static long Long(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.TryGetInt64(out var l) ? l : 0;

    public static int Int(JsonElement e, string n, int missing) =>
        e.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : missing;

    public static JsonElement Obj(JsonElement e, string n) =>
        e.TryGetProperty(n, out var o) && o.ValueKind == JsonValueKind.Object ? o : default;

    // a missing object (default element) has no arrays
    public static List<T> Arr<T>(JsonElement e, string n, Func<JsonElement, T> item) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(item).ToList() : [];
}
