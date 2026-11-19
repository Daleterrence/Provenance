using System.Text;

namespace Provenance.Common.PolFormat;

// each is XORed with its own file name as a repeating ASCII key, then UTF-16LE
public static class ProfilePictureCatalog
{
    public sealed record Category(string Name, string Key, int RegionMask);

    public sealed record ProfilePicture(int Id, string Name, string Artist, string CategoryKey);

    public sealed record Catalog(IReadOnlyList<Category> Categories, IReadOnlyList<ProfilePicture> ProfilePictures);

    public static string Decode(byte[] data, string fileName)
    {
        var key = Encoding.ASCII.GetBytes(fileName);
        var plain = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) plain[i] = (byte)(data[i] ^ key[i % key.Length]);
        int start = plain.Length >= 2 && plain[0] == 0xFF && plain[1] == 0xFE ? 2 : 0;
        return Encoding.Unicode.GetString(plain, start, plain.Length - start);
    }

    public static Catalog? Load(string viewerDir, int? region = null)
    {
        region ??= PolEnv.AreaIndex(ViewerBuild.RegionOf(viewerDir));
        var dir = new[] { Path.Combine(viewerDir, "viewer", "data", "common"), Path.Combine(viewerDir, "data", "common") }
                  .FirstOrDefault(d => File.Exists(Path.Combine(d, "proface0.txt")));
        if (dir is null) return null;
        var p0 = Path.Combine(dir, "proface0.txt");
        var p1 = Path.Combine(dir, "proface1.txt");
        if (!File.Exists(p0) || !File.Exists(p1)) return null;
        return Parse(Decode(File.ReadAllBytes(p0), "proface0.txt"), Decode(File.ReadAllBytes(p1), "proface1.txt"),
                     region.Value, sheet => ProfilePictureSheets.SheetPath(viewerDir, sheet) != null);
    }

    public static Catalog Parse(string categoriesText, string picsText, int region, Func<int, bool> sheetOnDisk)
    {
        var cats = new List<Category>();
        foreach (var f in Lines(categoriesText).Select(l => l.Split('\t')))
            if (f.Length >= 3 && int.TryParse(f[2].Trim(), out var mask) && (mask & (1 << region)) != 0)
                cats.Add(new Category(f[0].Trim(), f[1].Trim(), mask));
        var allowed = cats.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var pics = new List<ProfilePicture>();
        int sheet = -1, cell = 0;
        string catKey = "";
        foreach (var f in Lines(picsText).Select(l => l.Split('\t')))
        {
            if (f.Length < 5) continue;
            if (f[3].Trim().Length > 0 && int.TryParse(f[3].Trim(), out var s))
            {
                sheet = s;
                cell = 0;
                catKey = f[1].Trim();
            }
            else cell++;
            if (sheet < 0 || cell >= ProfilePictureSheets.PerSheet) continue;
            if (f[0].Trim() == "1" && allowed.Contains(catKey) && sheetOnDisk(sheet))
                pics.Add(new ProfilePicture(ProfilePictureSheets.IdOf(sheet, cell), f[4].Trim(), f.Length > 5 ? f[5].Trim() : "", catKey));
        }
        var used = pics.Select(x => x.CategoryKey).ToHashSet(StringComparer.Ordinal);
        return new Catalog(cats.Where(c => used.Contains(c.Key)).ToList(), pics);
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);
}
