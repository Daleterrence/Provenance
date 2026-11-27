using System.Text.Json;
using System.Text.Json.Nodes;

namespace Provenance.Common.Settings;

internal static class SettingsDiff
{
    // only the name for text and lists, they can hold a path or a character's name
    internal static string Changed(JsonObject? before, JsonObject after)
    {
        if (before is null) return "a new file";
        var changed = before.Select(p => p.Key).Union(after.Select(p => p.Key))
            .Where(k => !JsonNode.DeepEquals(before[k], after[k]))
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => after[k]?.GetValueKind() is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number ? $"{k}={after[k]!.ToJsonString()}" : k)
            .ToList();
        return changed.Count == 0 ? "nothing changed" : string.Join(", ", changed);
    }
}
