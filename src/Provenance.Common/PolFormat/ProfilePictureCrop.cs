namespace Provenance.Common.PolFormat;

// all in a picture's cell, 64x96 with x right and y down
public sealed record ProfilePictureCrop(double Cx, double Cy, double Diameter)
{
    public static readonly ProfilePictureCrop Default = new(32, 44.8, 64);

    public sealed record Region(double X, double Y, double Size);

    public static ProfilePictureCrop For(string setKey, IReadOnlyDictionary<string, ProfilePictureCrop>? table = null) =>
        (table ?? ProfilePictureCropTable.Crops).TryGetValue(setKey, out var crop) ? crop : Default;

    // the circle is made smaller and moved until real pixels cover all of it
    public Region? Within(double left, double top, double width, double height)
    {
        var d = Math.Min(Diameter, Math.Min(width, height));
        if (!(d > 0)) return null;
        var r = d / 2;
        var cx = Math.Clamp(Cx, left + r, left + width - r);
        var cy = Math.Clamp(Cy, top + r, top + height - r);
        return new Region(cx - r, cy - r, d);
    }
}
