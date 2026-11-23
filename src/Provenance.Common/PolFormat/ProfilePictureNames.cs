namespace Provenance.Common.PolFormat;

public sealed class ProfilePictureNames(IReadOnlyDictionary<string, string> sets, IReadOnlyDictionary<(string Set, int Id), string> pictures)
{
    public static ProfilePictureNames Default { get; } = new(ProfilePictureNameTable.Sets, ProfilePictureNameTable.Pictures);

    public string Set(string key, string name) => Wanted(name) && sets.TryGetValue(key, out var english) ? english : name;

    public string Picture(string key, int id, string name) => Wanted(name) && pictures.TryGetValue((key, id), out var english) ? english : name;

    // the NA catalogue titles the Japan-only sets "null"
    private static bool Wanted(string name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Equals("null", StringComparison.OrdinalIgnoreCase)
        || (name.Any(c => c > 127) && !name.Any(char.IsAsciiLetter));
}
