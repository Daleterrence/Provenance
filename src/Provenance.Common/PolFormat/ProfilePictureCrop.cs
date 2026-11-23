namespace Provenance.Common.PolFormat;

// all as fractions of the circle's diameter, the image is Zoom wide and 1.5 times that tall
public sealed record ProfilePictureCrop(double Top, double Zoom, double Left)
{
    public static readonly ProfilePictureCrop Default = new(-0.2, 1.0, 0.0);

    public static ProfilePictureCrop For(string setKey, IReadOnlyDictionary<string, ProfilePictureCrop>? table = null) =>
        (table ?? ProfilePictureCropTable.Crops).TryGetValue(setKey, out var crop) ? crop : Default;
}
