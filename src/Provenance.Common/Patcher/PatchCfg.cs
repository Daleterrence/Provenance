using System.Buffers;
using System.Security.Cryptography;
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

public static class PatchCfg
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

// POL's file signature (worker op 15): signed-char byte sum, first 4 MD5 bytes as a LE int, length
public readonly record struct FileSig(int Sum, int Md5Word, long Length)
{
    public static FileSig Of(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Of(fs, ct);
    }

    public static FileSig Of(Stream s, CancellationToken ct = default)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            int sum = 0, n;
            long len = 0;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                md5.AppendData(buf, 0, n);
                for (int i = 0; i < n; i++) sum = unchecked(sum + (sbyte)buf[i]);
                len += n;
            }
            return new FileSig(sum, BitConverter.ToInt32(md5.GetHashAndReset(), 0), len);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }
}

// POL's version value (app.dll 0x1029c0ca) and its up-to-date test (0x1029c057)
public static class Base37
{
    public static long Value(string s)
    {
        long v = 0;
        foreach (char ch in s)
        {
            if (ch == '\0' || ch == ' ') break;
            int c = (sbyte)(byte)ch;   // signed char, same as POL, don't "fix" it
            if (c == '_') continue;
            int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'A' and <= 'Z' ? c - 'A' + 10 : 0;
            v = unchecked(v * 37 + d);
        }
        return v;
    }

    public static bool UpToDate(string local, string server) =>
        local.Length == server.Length && Value(local) >= Value(server);
}
