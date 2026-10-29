using System.Text;
using System.Text.Json;

namespace Provenance.Common.Social;

public sealed record MyProfileField(string Label, int Level, long Value, string Text);

public sealed record MyProfileChoice(long Code, IReadOnlyList<string> Parts)
{
    public string Text => string.Join(" \u203a ", Parts.Where(p => p.Length > 0));
}

// Level is 1 Private, 2 Friends only, 3 POL. Tables are the viewer's value tables (prof_pol.pfb)
public sealed record MyProfile(string Handle, int Level, IReadOnlyDictionary<string, MyProfileField> Fields,
                               IReadOnlyDictionary<string, IReadOnlyList<MyProfileChoice>> Tables)
{
    public string Token { get; init; } = "";

    public static string PathForPid(int pid) => HookFiles.PathFor(pid, "myprofile.json");

    // the hook keeps this file for debugging, so the e-mail address from the POL profile (z_mail) comes in
    // masked, first letter, *** and the domain, never the real one
    public static MyProfile? TryRead(int pid)
    {
        var path = PathForPid(pid);
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
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
            int level = root.TryGetProperty("level", out var lev) ? lev.GetInt32() : 0;
            return new MyProfile(Text(root, "handle"), level, fields, tables)
            {
                Token = Text(root, "token"),
            };
        }
        catch { return null; }

        static string Text(JsonElement e, string n) => e.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
    }
}
