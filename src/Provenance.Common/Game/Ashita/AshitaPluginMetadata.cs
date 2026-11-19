using System.Buffers.Binary;

namespace Provenance.Common.Game.Ashita;

// every read is bounds-checked, anyone can drop a file in plugins\
internal sealed class AshitaPluginMetadata
{
    private const int MaxSections = 32, MaxCandidates = 1024, MaxRemembered = 1 << 16;
    private const long MaxScanned = 16L << 20;

    private readonly PeFile pe;
    private readonly PeFile.Section[] code;
    private readonly Dictionary<ulong, (Shape, uint)> getters = [];

    private AshitaPluginMetadata(PeFile pe)
    {
        this.pe = pe;
        code = pe.Sections.Where(s => (s.Flags & (PeFile.CodeFlag | PeFile.ExecuteFlag)) != 0).ToArray();
    }

    public IReadOnlyDictionary<string, uint> Exports => pe.Exports;

    public static AshitaPluginMetadata? Load(string path, long maxSize) => From(PeFile.Load(path, maxSize, MaxSections));

    public static AshitaPluginMetadata? Parse(byte[] d) => From(PeFile.Parse(d, MaxSections));

    private static AshitaPluginMetadata? From(PeFile? pe) => pe is { Machine: PeFile.MachineI386, Is32Bit: true } ? new AshitaPluginMetadata(pe) : null;

    public double? ExportedDouble(uint rva) => Double(pe.ImageBase + rva);

    // a plugin's vtable is four string getters then a double getter, bar the SDK's own base classes
    public (string Name, string Author, string Description, double Version)? Vtable()
    {
        long budget = MaxScanned;
        int candidates = 0;
        foreach (var (from, to) in DataRanges())
            for (long at = from; at + 20 <= to; at += 4)
            {
                if ((budget -= 4) < 0) return null;
                uint name = PeFile.U32(pe.Bytes, at), author = PeFile.U32(pe.Bytes, at + 4), description = PeFile.U32(pe.Bytes, at + 8), link = PeFile.U32(pe.Bytes, at + 12),
                     version = PeFile.U32(pe.Bytes, at + 16);
                if (Getter(name).Shape != Shape.Pointer || Getter(author).Shape != Shape.Pointer || Getter(description).Shape != Shape.Pointer ||
                    Getter(link).Shape != Shape.Pointer || Getter(version).Shape is not (Shape.Double or Shape.Float or Shape.One or Shape.Zero))
                    continue;
                if (++candidates > MaxCandidates) return null;
                if (String(name) is not { } n || n is "PluginBase" or "PolPluginBase") continue;
                if (String(author) is { } a && String(description) is { } d && String(link) is not null && Double(version) is { } v) return (n, a, d, v);
            }
        return null;
    }

    private List<(long From, long To)> DataRanges()
    {
        var ranges = pe.Sections.Where(s => (s.Flags & PeFile.DataFlag) != 0 && (s.Flags & PeFile.ExecuteFlag) == 0)
            .Select(s => (From: (long)s.Offset, To: s.Offset + (long)s.Size)).OrderBy(r => r.From).ToList();
        var merged = new List<(long From, long To)>();
        foreach (var r in ranges)
            if (merged.Count > 0 && r.From <= merged[^1].To) merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, r.To));
            else merged.Add(r);
        return merged;
    }

    private enum Shape { None, Pointer, Double, Float, One, Zero }

    private (Shape Shape, uint Value) Getter(ulong va)
    {
        if (getters.TryGetValue(va, out var known)) return known;
        var found = Decode(va);
        if (getters.Count < MaxRemembered) getters[va] = found;
        return found;
    }

    // a getter is "mov eax, imm32" (strings) or an fld (doubles) then ret, maybe behind a push ebp frame or a jmp
    private (Shape Shape, uint Value) Decode(ulong va)
    {
        for (int hops = 0; hops < 3; hops++)
        {
            var b = CodeAt(va, 24);
            if (b.IsEmpty) return default;
            if (b.Length >= 5 && b[0] == 0xE9)
            {
                va = (uint)(va + 5 + PeFile.Read32(b, 1));
                continue;
            }
            bool framed = b.StartsWith((ReadOnlySpan<byte>)[0x55, 0x8B, 0xEC]);
            if (framed) b = b[3..].StartsWith((ReadOnlySpan<byte>)[0x51, 0x89, 0x4D, 0xFC]) ? b[7..] : b[3..];
            var (shape, value, length) = b switch
            {
                [0xB8, _, _, _, _, ..] => (Shape.Pointer, PeFile.Read32(b, 1), 5),
                [0xDD, 0x05, _, _, _, _, ..] => (Shape.Double, PeFile.Read32(b, 2), 6),
                [0xD9, 0x05, _, _, _, _, ..] => (Shape.Float, PeFile.Read32(b, 2), 6),
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

    private string? String(ulong va) => Getter(va) is (Shape.Pointer, var at) ? pe.TextAtVa(at, 512) : null;

    private double? Double(ulong va)
    {
        var (shape, at) = Getter(va);
        double? v = shape switch
        {
            Shape.One => 1.0,
            Shape.Zero => 0.0,
            Shape.Double when pe.AtVa(at, 8) is { Length: 8 } b => BinaryPrimitives.ReadDoubleLittleEndian(b),
            Shape.Float when pe.AtVa(at, 4) is { Length: 4 } b => BinaryPrimitives.ReadSingleLittleEndian(b),
            _ => null,
        };
        return v is { } d && double.IsFinite(d) ? d : null;
    }

    private ReadOnlySpan<byte> CodeAt(ulong va, int max)
    {
        if (va < pe.ImageBase) return default;
        ulong rva = va - pe.ImageBase;
        foreach (var s in code)
            if (rva >= s.Rva && rva - s.Rva < s.Size) return pe.Bytes.AsSpan(s.Offset + (int)(rva - s.Rva), (int)Math.Min(max, s.Size - (long)(rva - s.Rva)));
        return default;
    }

}
