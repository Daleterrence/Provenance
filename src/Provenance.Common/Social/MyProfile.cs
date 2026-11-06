using System.Text.Json;
using Provenance.Common.Hook;

namespace Provenance.Common.Social;

public sealed record MyProfileField(string Label, int Level, long Value, string Text);

public sealed record MyProfileChoice(long Code, IReadOnlyList<string> Parts);

// one of my FFXI characters, as the viewer's editor tabs have them. Key is the hook's for asking about it, Name is empty
// when the Content ID list doesn't have one
public sealed record MyProfileCharacter(int Slot, string Key, int World, string Name)
{
    public string? WorldName => FfxiWorlds.Name(World);
}

// Level is 1 Private, 2 Friends only, 3 POL. Tables are the viewer's value tables (prof_pol.pfb)
public sealed record MyProfile(string Handle, int Level, IReadOnlyDictionary<string, MyProfileField> Fields,
                               IReadOnlyDictionary<string, IReadOnlyList<MyProfileChoice>> Tables)
{
    public string Token { get; init; } = "";
    // the H-ID of the handle it was loaded for, a rename's refused for any other
    public string Key { get; init; } = "";
    public IReadOnlyList<MyProfileCharacter> Characters { get; init; } = [];

    public static string PathForPid(int pid) => HookFiles.PathFor(pid, HookContract.Files.MyProfile);

    // the e-mail address from the POL profile (z_mail) comes in masked, first letter, *** and the domain, never the
    // real one
    public static MyProfile? TryRead(int pid) =>
        HookFiles.ReadJson(pid, HookContract.Files.MyProfile, HookFiles.MaxBytes.MyProfile, Parse);

    public static MyProfile? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var fields = new Dictionary<string, MyProfileField>();
            if (root.TryGetProperty("fields", out var fs))
                foreach (var f in fs.EnumerateObject())
                    fields[f.Name] = new MyProfileField(Text(f.Value, "label"),
                        f.Value.TryGetProperty("level", out var lv) ? lv.GetInt32() : 0,
                        f.Value.TryGetProperty("v", out var v) ? v.GetInt64() : 0, Text(f.Value, "s"));
            var tables = new Dictionary<string, IReadOnlyList<MyProfileChoice>>();
            if (root.TryGetProperty("tables", out var ts))
                foreach (var t in ts.EnumerateObject())
                {
                    var list = new List<MyProfileChoice>();
                    foreach (var row in t.Value.EnumerateArray())
                        if (row.EnumerateArray().ToList() is { Count: >= 2 } items)
                            list.Add(new MyProfileChoice(items[0].GetInt64(),
                                                         items.Skip(1).Select(x => x.GetString() ?? "").ToList()));
                    tables[t.Name] = list;
                }
            var characters = new List<MyProfileCharacter>();
            if (root.TryGetProperty("characters", out var cs))
                foreach (var c in cs.EnumerateArray())
                    if (Text(c, "key") is { Length: > 0 } key)
                        characters.Add(new MyProfileCharacter(c.TryGetProperty("slot", out var sl) ? sl.GetInt32() : -1, key,
                                                              c.TryGetProperty("world", out var w) ? w.GetInt32() : 0,
                                                              Text(c, "name").Trim()));
            int level = root.TryGetProperty("level", out var lev) ? lev.GetInt32() : 0;
            return new MyProfile(Text(root, "handle"), level, fields, tables)
            {
                Token = Text(root, "token"),
                Key = Text(root, "key"),
                Characters = characters,
            };
        }
        catch { return null; }

        static string Text(JsonElement e, string n) => e.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
    }
}
