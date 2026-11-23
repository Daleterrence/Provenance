using System.Globalization;
using System.Text.Json;

namespace Provenance.Common.PolFormat;

// pictures.json: {"2400": {"x0": -16, "y0": 0, "scale": 2}}, where x0 and y0 are the image's top-left in the cell and scale is image pixels per cell pixel
public sealed record ProfilePictureAssetMeta(double X0, double Y0, double Scale)
{
    public static Dictionary<int, ProfilePictureAssetMeta> Parse(string json)
    {
        var all = new Dictionary<int, ProfilePictureAssetMeta>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return all;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || p.Value.ValueKind != JsonValueKind.Object) continue;
                if (Number(p.Value, "x0") is { } x0 && Number(p.Value, "y0") is { } y0 && Number(p.Value, "scale") is { } scale && scale > 0)
                    all[id] = new ProfilePictureAssetMeta(x0, y0, scale);
            }
        }
        catch (JsonException) { all.Clear(); }
        return all;
    }

    private static double? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
}
