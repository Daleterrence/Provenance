using System.Text;
using System.Text.RegularExpressions;

namespace Provenance.Common.Patcher;

// directive flags 2, 0x80, 0x40, 4. The names go into patch2.cfg as they are, don't rename them.
public enum BlockAttribute { None, Reset, Reset2, Option, Select }

// "<stamp> <len> <sum> <md5w> <stamp>/Direct/<p>.slc <slcLen> [<stamp>/Indirect/<p>.olc <olcLen>]". Paths can hold
// spaces, POL's tokenizer keeps them.
public sealed record PatchLine(string Stamp, FileSig Sig, string SlcPath, long SlcLen, string? OlcPath, long OlcLen)
{
    public override string ToString() =>
        $"{Stamp} {Sig.Length} {Sig.Sum} {Sig.Md5Word} {SlcPath} {SlcLen}" + (OlcPath is null ? "" : $" {OlcPath} {OlcLen}");
}

public sealed record PatchFile(string Name, BlockAttribute Attribute, IReadOnlyList<PatchLine> Lines)
{
    public PatchLine Last => Lines[^1];
}

public static class PatchFileList
{
    private static readonly Regex LineRx = new(
        @"^(\S+) (-?\d+) (-?\d+) (-?\d+) (.+?\.slc) (\d+)(?: (.+?\.olc) (\d+))?\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex MarkerRx = new(@"__([A-Za-z0-9]{3})__", RegexOptions.CultureInvariant);

    // a "__XXX__" region marker keeps the file only when XXX is tagFilter (app.dll 0x102a5bec and 0x102a5c83)
    public static List<PatchFile> Parse(string text, string tagFilter)
    {
        var files = new List<PatchFile>();
        var attr = BlockAttribute.None;
        string? name = null;
        List<PatchLine>? lines = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (name != null)
            {
                if (line == "}")
                {
                    if (lines!.Count > 0 && Filter(name, tagFilter) is { } kept) files.Add(new PatchFile(kept, attr, lines));
                    name = null;
                    continue;
                }
                var m = LineRx.Match(line);
                if (!m.Success) throw new InvalidDataException($"patch.cfg: unreadable line in {name}: {line}");
                lines!.Add(new PatchLine(m.Groups[1].Value,
                    new FileSig(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), long.Parse(m.Groups[2].Value)),
                    m.Groups[5].Value, long.Parse(m.Groups[6].Value),
                    m.Groups[7].Success ? m.Groups[7].Value : null, m.Groups[8].Success ? long.Parse(m.Groups[8].Value) : 0));
                continue;
            }
            if (line.StartsWith("file ", StringComparison.OrdinalIgnoreCase) && line.EndsWith('{'))
            {
                name = line[5..^1].Trim();
                lines = [];
            }
            else if (line == "}") attr = BlockAttribute.None;
            else if (line.Equals("end", StringComparison.OrdinalIgnoreCase)) break;
            else if (line.EndsWith('{'))
            {
                attr = line[..^1].Trim().ToLowerInvariant() switch
                {
                    "reset" => BlockAttribute.Reset,
                    "reset2" => BlockAttribute.Reset2,
                    "option" => BlockAttribute.Option,
                    "select" => BlockAttribute.Select,
                    _ => throw new InvalidDataException($"patch.cfg: unknown block {line}"),
                };
            }
        }
        return files;
    }

    private static string? Filter(string name, string tagFilter)
    {
        var m = MarkerRx.Match(name);
        if (!m.Success) return name;
        return m.Groups[1].Value.Equals(tagFilter, StringComparison.Ordinal) ? MarkerRx.Replace(name, "", 1) : null;
    }

    public static string Write(IEnumerable<PatchFile> files)
    {
        var sb = new StringBuilder("\n");
        foreach (var f in files)
        {
            var wrap = f.Attribute == BlockAttribute.None ? null : f.Attribute.ToString();
            if (wrap != null) sb.Append(wrap).Append(" {\n");
            sb.Append("file ").Append(f.Name).Append(" {\n");
            foreach (var l in f.Lines) sb.Append(l).Append('\n');
            sb.Append("}\n");
            if (wrap != null) sb.Append("}\n");
            sb.Append('\n');
        }
        return sb.Append("end\n\n").ToString();
    }
}
