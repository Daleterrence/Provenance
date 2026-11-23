using System.Text;

namespace Provenance.Common.PolFormat;

// each is XORed with its own file name as a repeating ASCII key, then UTF-16LE
public static class ProfilePictureCatalog
{
    public sealed record Category(string Name, string Key, int RegionMask);

    public sealed record ProfilePicture(int Id, string Name, string Artist, string CategoryKey);

    public sealed record Catalog(IReadOnlyList<Category> Categories, IReadOnlyList<ProfilePicture> ProfilePictures)
    {
        private Dictionary<int, ProfilePicture>? _byId;

        public ProfilePicture? Find(int id) =>
            (_byId ??= ProfilePictures.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First())).GetValueOrDefault(id);

        public bool Offers(int id) => Find(id) is not null;
    }

    public static string Decode(byte[] data, string fileName)
    {
        var key = Encoding.ASCII.GetBytes(fileName);
        var plain = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) plain[i] = (byte)(data[i] ^ key[i % key.Length]);
        int start = plain.Length >= 2 && plain[0] == 0xFF && plain[1] == 0xFE ? 2 : 0;
        return Encoding.Unicode.GetString(plain, start, plain.Length - start);
    }

    public static Catalog? Load(string viewerDir, int? region = null) => Load(viewerDir, ProfilePictureExtras.Folder, region);

    public static Catalog? Load(string viewerDir, string? extrasDir, int? region = null)
    {
        region ??= PolEnv.AreaIndex(ViewerBuild.RegionOf(viewerDir));
        var dir = new[] { Path.Combine(viewerDir, "viewer", "data", "common"), Path.Combine(viewerDir, "data", "common") }
                  .FirstOrDefault(d => File.Exists(Path.Combine(d, "proface0.txt")));
        if (dir is null) return null;
        var p0 = Path.Combine(dir, "proface0.txt");
        var p1 = Path.Combine(dir, "proface1.txt");
        if (!File.Exists(p0) || !File.Exists(p1)) return null;
        string? jp0 = null, jp1 = null;
        if (extrasDir is not null)
        {
            var e0 = Path.Combine(extrasDir, "viewer", "data", "common", "proface0.txt");
            var e1 = Path.Combine(extrasDir, "viewer", "data", "common", "proface1.txt");
            if (File.Exists(e0) && File.Exists(e1))
            {
                jp0 = Decode(File.ReadAllBytes(e0), "proface0.txt");
                jp1 = Decode(File.ReadAllBytes(e1), "proface1.txt");
            }
        }
        return ParseUnion(Decode(File.ReadAllBytes(p0), "proface0.txt"), Decode(File.ReadAllBytes(p1), "proface1.txt"), jp0, jp1,
                          region.Value, sheet => ProfilePictureSheets.SheetPath(viewerDir, extrasDir, sheet) != null);
    }

    // names are SE's own: the local catalogue's unless it says null, nothing or only Japanese, then the Japan one's as it comes
    public static Catalog ParseUnion(string categoriesText, string picsText, string? jpCategoriesText, string? jpPicsText, int region,
                                     Func<int, bool> sheetOnDisk)
    {
        var local = Parse(categoriesText, picsText, region, sheetOnDisk);
        if (jpCategoriesText is null || jpPicsText is null) return local;
        var jp = Parse(jpCategoriesText, jpPicsText, PolEnv.AreaIndex(PolRegion.Japan), sheetOnDisk);
        var cats = local.Categories.ToList();
        var keys = cats.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        cats.AddRange(jp.Categories.Where(c => keys.Add(c.Key)));
        var pics = local.ProfilePictures.ToList();
        var ids = pics.Select(p => p.Id).ToHashSet();
        pics.AddRange(jp.ProfilePictures.Where(p => ids.Add(p.Id)));

        var localTitles = Titles(categoriesText);
        var jpTitles = Titles(jpCategoriesText);
        var localNames = Rows(picsText).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Name);
        var jpNames = Rows(jpPicsText).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Name);
        return new Catalog(
            cats.Select(c => c with { Name = Pick(localTitles.GetValueOrDefault(c.Key), jpTitles.GetValueOrDefault(c.Key)) ?? c.Name }).ToList(),
            pics.Select(p => p with { Name = Pick(localNames.GetValueOrDefault(p.Id), jpNames.GetValueOrDefault(p.Id)) ?? p.Name }).ToList());
    }

    private static string? Pick(string? local, string? jp) => local is not null && Usable(local) ? local : jp ?? local;

    private static bool Usable(string name) =>
        !string.IsNullOrWhiteSpace(name) && !name.Trim().Equals("null", StringComparison.OrdinalIgnoreCase)
        && !(name.Any(c => c > 127) && !name.Any(char.IsAsciiLetter));

    private static Dictionary<string, string> Titles(string categoriesText)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Lines(categoriesText).Select(l => l.Split('\t')))
            if (f.Length >= 3) titles.TryAdd(f[1].Trim(), f[0].Trim());
        return titles;
    }

    private sealed record Row(int Id, string Key, bool On, string Name, string Artist);

    private static IEnumerable<Row> Rows(string picsText)
    {
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
            yield return new Row(ProfilePictureSheets.IdOf(sheet, cell), catKey, f[0].Trim() == "1", f[4].Trim(), f.Length > 5 ? f[5].Trim() : "");
        }
    }

    public static Catalog Parse(string categoriesText, string picsText, int region, Func<int, bool> sheetOnDisk)
    {
        var cats = new List<Category>();
        foreach (var f in Lines(categoriesText).Select(l => l.Split('\t')))
            if (f.Length >= 3 && int.TryParse(f[2].Trim(), out var mask) && (mask & (1 << region)) != 0)
                cats.Add(new Category(f[0].Trim(), f[1].Trim(), mask));
        var allowed = cats.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var pics = Rows(picsText).Where(r => r.On && allowed.Contains(r.Key) && sheetOnDisk(ProfilePictureSheets.SheetOf(r.Id)))
            .Select(r => new ProfilePicture(r.Id, r.Name, r.Artist, r.Key)).ToList();
        var used = pics.Select(x => x.CategoryKey).ToHashSet(StringComparer.Ordinal);
        return new Catalog(cats.Where(c => used.Contains(c.Key)).ToList(), pics);
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0);
}
