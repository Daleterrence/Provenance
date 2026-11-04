using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;

namespace Provenance.Common.Game.Ashita;

// just enough of a 32-bit PE to read its exports and a few constants without loading it. Every read is bounds-checked,
// anyone can drop a file in plugins\
internal sealed class PluginImage
{
    private const ushort I386 = 0x14c, Pe32 = 0x10b;
    private const uint CodeFlag = 0x20, DataFlag = 0x40, ExecuteFlag = 0x20000000;
    private const int MaxSections = 32, MaxCandidates = 1024, MaxRemembered = 1 << 16;
    private const uint MaxExports = 4096;
    private const long MaxScanned = 16L << 20;

    private readonly record struct Section(uint Rva, uint Size, int Offset, uint Flags);

    private readonly byte[] data;
    private readonly Section[] sections, code;
    private readonly Dictionary<ulong, (Shape, uint)> getters = [];
    private readonly uint exportRva;
    private readonly uint imageBase;

    private PluginImage(byte[] data, uint imageBase, Section[] sections, uint exportRva)
    {
        this.data = data;
        this.imageBase = imageBase;
        this.sections = sections;
        code = sections.Where(s => (s.Flags & (CodeFlag | ExecuteFlag)) != 0).ToArray();
        this.exportRva = exportRva;
    }

    public static PluginImage? Load(string path, long maxSize)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > maxSize) return null;
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            return Parse(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static PluginImage? Parse(byte[] d)
    {
        if (d.Length < 0x40 || d[0] != 'M' || d[1] != 'Z') return null;
        long pe = U32(d, 0x3c);
        if (pe + 24 > d.Length || U32(d, pe) != 0x00004550 || U16(d, pe + 4) != I386) return null;
        int count = Math.Min((int)U16(d, pe + 6), MaxSections), optSize = U16(d, pe + 20);
        long opt = pe + 24;
        if (optSize < 96 || opt + optSize > d.Length || U16(d, opt) != Pe32) return null;
        uint imageBase = U32(d, opt + 28);
        uint exportRva = U32(d, opt + 92) >= 1 && optSize >= 104 ? U32(d, opt + 96) : 0;
        var list = new List<Section>();
        for (long i = 0, s = opt + optSize; i < count && s + 40 <= d.Length; i++, s += 40)
        {
            uint virtualSize = U32(d, s + 8), rva = U32(d, s + 12), raw = U32(d, s + 16), ptr = U32(d, s + 20);
            if (ptr >= d.Length) continue;
            long size = Math.Min(raw, d.Length - ptr);
            if (virtualSize != 0) size = Math.Min(size, virtualSize);
            if (size > 0) list.Add(new Section(rva, (uint)size, (int)ptr, U32(d, s + 36)));
        }
        return new PluginImage(d, imageBase, list.ToArray(), exportRva);
    }

    public Dictionary<string, uint> Exports()
    {
        var map = new Dictionary<string, uint>(StringComparer.Ordinal);
        var dir = AtRva(exportRva, 40);
        if (exportRva == 0 || dir.Length < 40) return map;
        uint functions = Read32(dir, 20), names = Read32(dir, 24), funcTable = Read32(dir, 28), nameTable = Read32(dir, 32), ordTable = Read32(dir, 36);
        for (uint i = 0; i < Math.Min(names, MaxExports); i++)
        {
            var np = AtRva(nameTable + 4UL * i, 4);
            var op = AtRva(ordTable + 2UL * i, 2);
            if (np.Length < 4 || op.Length < 2) break;
            uint ordinal = Read16(op, 0);
            if (ordinal >= functions) continue;
            var fp = AtRva(funcTable + 4UL * ordinal, 4);
            if (fp.Length == 4 && Text((ulong)imageBase + Read32(np, 0), 256) is { } name) map.TryAdd(name, Read32(fp, 0));
        }
        return map;
    }

    public double? ExportedDouble(uint rva) => Double((ulong)imageBase + rva);

    // four string getters then a double getter (IPluginBase's GetName, GetAuthor, GetDescription, GetLink, GetVersion)
    // is a plugin's vtable, bar the SDK's own base classes. Bit hacky, but nothing gets loaded
    public (string Name, string Author, string Description, double Version)? Vtable()
    {
        long budget = MaxScanned;
        int candidates = 0;
        foreach (var (from, to) in DataRanges())
            for (long at = from; at + 20 <= to; at += 4)
            {
                if ((budget -= 4) < 0) return null;
                uint name = U32(data, at), author = U32(data, at + 4), description = U32(data, at + 8), link = U32(data, at + 12),
                     version = U32(data, at + 16);
                if (Getter(name).Shape != Shape.Pointer || Getter(author).Shape != Shape.Pointer || Getter(description).Shape != Shape.Pointer ||
                    Getter(link).Shape != Shape.Pointer || Getter(version).Shape is not (Shape.Double or Shape.Float or Shape.One or Shape.Zero))
                    continue;
                if (++candidates > MaxCandidates) return null;
                if (String(name) is not { } n || n is "PluginBase" or "PolPluginBase") continue;
                if (String(author) is { } a && String(description) is { } d && String(link) is not null && Double(version) is { } v) return (n, a, d, v);
            }
        return null;
    }

    // overlapping sections are only read once
    private List<(long From, long To)> DataRanges()
    {
        var ranges = sections.Where(s => (s.Flags & DataFlag) != 0 && (s.Flags & ExecuteFlag) == 0)
            .Select(s => (From: (long)s.Offset, To: s.Offset + (long)s.Size)).OrderBy(r => r.From).ToList();
        var merged = new List<(long From, long To)>();
        foreach (var r in ranges)
            if (merged.Count > 0 && r.From <= merged[^1].To) merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, r.To));
            else merged.Add(r);
        return merged;
    }

    private enum Shape { None, Pointer, Double, Float, One, Zero }

    // vtables point at the same few functions over and over
    private (Shape Shape, uint Value) Getter(ulong va)
    {
        if (getters.TryGetValue(va, out var known)) return known;
        var found = Decode(va);
        if (getters.Count < MaxRemembered) getters[va] = found;
        return found;
    }

    // a getter is "mov eax, imm32" (strings) or an fld (doubles) then ret. Unoptimised builds wrap it in the push ebp
    // frame, incremental linking puts a jmp in front
    private (Shape Shape, uint Value) Decode(ulong va)
    {
        for (int hops = 0; hops < 3; hops++)
        {
            var b = CodeAt(va, 24);
            if (b.IsEmpty) return default;
            if (b.Length >= 5 && b[0] == 0xE9)
            {
                va = (uint)(va + 5 + Read32(b, 1));
                continue;
            }
            bool framed = b.StartsWith((ReadOnlySpan<byte>)[0x55, 0x8B, 0xEC]);
            if (framed) b = b[3..].StartsWith((ReadOnlySpan<byte>)[0x51, 0x89, 0x4D, 0xFC]) ? b[7..] : b[3..];
            var (shape, value, length) = b switch
            {
                [0xB8, _, _, _, _, ..] => (Shape.Pointer, Read32(b, 1), 5),
                [0xDD, 0x05, _, _, _, _, ..] => (Shape.Double, Read32(b, 2), 6),
                [0xD9, 0x05, _, _, _, _, ..] => (Shape.Float, Read32(b, 2), 6),
                [0xD9, 0xE8, ..] => (Shape.One, 0u, 2),
                [0xD9, 0xEE, ..] => (Shape.Zero, 0u, 2),
                _ => (Shape.None, 0u, 0),
            };
            if (shape == Shape.None) return default;
            var tail = b[length..];
            bool ret = framed
                ? tail.StartsWith((ReadOnlySpan<byte>)[0x8B, 0xE5, 0x5D, 0xC3]) || tail.StartsWith((ReadOnlySpan<byte>)[0x5D, 0xC3])
                : tail.StartsWith((ReadOnlySpan<byte>)[0xC3]);
            return ret ? (shape, value) : default;
        }
        return default;
    }

    private string? String(ulong va) => Getter(va) is (Shape.Pointer, var at) ? Text(at, 512) : null;

    private double? Double(ulong va)
    {
        var (shape, at) = Getter(va);
        double? v = shape switch
        {
            Shape.One => 1.0,
            Shape.Zero => 0.0,
            Shape.Double when AtVa(at, 8) is { Length: 8 } b => BinaryPrimitives.ReadDoubleLittleEndian(b),
            Shape.Float when AtVa(at, 4) is { Length: 4 } b => BinaryPrimitives.ReadSingleLittleEndian(b),
            _ => null,
        };
        return v is { } d && double.IsFinite(d) ? d : null;
    }

    private string? Text(ulong va, int max)
    {
        var span = AtVa(va, max + 1);
        int end = span.IndexOf((byte)0);
        if (end < 0) return null;
        if (!Utf8.IsValid(span[..end])) return null;
        var s = Encoding.UTF8.GetString(span[..end]);
        foreach (var c in s)
            if (c < 0x20 && c is not ('\t' or '\r' or '\n') || c == 0x7f) return null;
        return s;
    }

    private ReadOnlySpan<byte> CodeAt(ulong va, int max)
    {
        if (va < imageBase) return default;
        ulong rva = va - imageBase;
        foreach (var s in code)
            if (rva >= s.Rva && rva - s.Rva < s.Size) return data.AsSpan(s.Offset + (int)(rva - s.Rva), (int)Math.Min(max, s.Size - (long)(rva - s.Rva)));
        return default;
    }

    private ReadOnlySpan<byte> AtVa(ulong va, int max) => va < imageBase ? default : AtRva(va - imageBase, max);

    private ReadOnlySpan<byte> AtRva(ulong rva, int max)
    {
        foreach (var s in sections)
            if (rva >= s.Rva && rva - s.Rva < s.Size)
            {
                long into = (long)(rva - s.Rva);
                return data.AsSpan(s.Offset + (int)into, (int)Math.Min(max, s.Size - into));
            }
        return default;
    }

    private static uint U32(byte[] d, long at) => at >= 0 && at + 4 <= d.Length ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)at)) : 0;
    private static ushort U16(byte[] d, long at) => at >= 0 && at + 2 <= d.Length ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)at)) : (ushort)0;
    private static uint Read32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    private static ushort Read16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
}
