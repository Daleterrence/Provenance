using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;

namespace Provenance.Common.Game;

// reads a DLL's sections, exports and version straight off the disk. Nothing gets loaded, not even as a datafile, so a
// plugin's code never runs. Anyone can drop a file in a plugin folder, so every read is bounds-checked
internal sealed class PeFile
{
    public const ushort MachineI386 = 0x014C;
    public const long DefaultMaxSize = 16L << 20;
    public const uint CodeFlag = 0x20, DataFlag = 0x40, ExecuteFlag = 0x20000000;

    // 96 sections is the loader's own limit, the other caps keep a crafted file cheap
    private const int MaxSections = 96, MaxResourceEntries = 4096;
    private const uint MaxExports = 16384, SubdirectoryBit = 0x80000000;

    public readonly record struct Section(uint Rva, uint Size, int Offset, uint Flags);

    public byte[] Bytes { get; }
    public ushort Machine { get; }
    public bool Is32Bit { get; }
    public ulong ImageBase { get; }
    public IReadOnlyList<Section> Sections => sections;

    private readonly Section[] sections;
    private readonly uint exportRva, resourceRva;
    private IReadOnlyDictionary<string, uint>? exports;
    private Version? fileVersion;
    private bool versionRead;

    private PeFile(byte[] bytes, ushort machine, bool is32Bit, ulong imageBase, Section[] sections, uint exportRva, uint resourceRva)
    {
        Bytes = bytes;
        Machine = machine;
        Is32Bit = is32Bit;
        ImageBase = imageBase;
        this.sections = sections;
        this.exportRva = exportRva;
        this.resourceRva = resourceRva;
    }

    // export name -> the function's RVA
    public IReadOnlyDictionary<string, uint> Exports => exports ??= ReadExports();

    public Version? FileVersion
    {
        get
        {
            if (!versionRead)
            {
                fileVersion = resourceRva == 0 ? null : ReadVersion(resourceRva);
                versionRead = true;
            }
            return fileVersion;
        }
    }

    public static Version? ReadFileVersion(string path) => Load(path)?.FileVersion;

    public static PeFile? Load(string path, long maxSize = DefaultMaxSize, int keepSections = MaxSections)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > maxSize) return null;
            var bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
            return Parse(bytes, keepSections);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // keepSections is for a caller that only trusts the first few
    public static PeFile? Parse(byte[] d, int keepSections = MaxSections)
    {
        if (d.Length < 0x40 || d[0] != 'M' || d[1] != 'Z') return null;
        long pe = U32(d, 0x3C);
        if (pe + 24 > d.Length || U32(d, pe) != 0x00004550) return null;
        int count = U16(d, pe + 6), optSize = U16(d, pe + 20);
        if (count > MaxSections) return null;
        count = Math.Min(count, keepSections);
        long opt = pe + 24;
        bool pe32 = U16(d, opt) == 0x10B;
        if (!pe32 && U16(d, opt) != 0x20B) return null;
        int dirs = pe32 ? 96 : 112;
        if (optSize < dirs || opt + optSize > d.Length) return null;
        ulong imageBase = pe32 ? U32(d, opt + 28) : BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan((int)opt + 24, 8));
        uint dirCount = U32(d, opt + dirs - 4);
        uint exportRva = dirCount > 0 && optSize >= dirs + 8 ? U32(d, opt + dirs) : 0;
        uint resourceRva = dirCount > 2 && optSize >= dirs + 24 ? U32(d, opt + dirs + 16) : 0;
        var list = new List<Section>();
        for (long i = 0, s = opt + optSize; i < count && s + 40 <= d.Length; i++, s += 40)
        {
            uint virtualSize = U32(d, s + 8), rva = U32(d, s + 12), raw = U32(d, s + 16), ptr = U32(d, s + 20);
            if (ptr >= d.Length) continue;
            long size = Math.Min(raw, d.Length - ptr);
            if (virtualSize != 0) size = Math.Min(size, virtualSize);
            if (size > 0) list.Add(new Section(rva, (uint)size, (int)ptr, U32(d, s + 36)));
        }
        return new PeFile(d, U16(d, pe + 4), pe32, imageBase, list.ToArray(), exportRva, resourceRva);
    }

    public ReadOnlySpan<byte> AtRva(ulong rva, int max)
    {
        foreach (var s in sections)
            if (rva >= s.Rva && rva - s.Rva < s.Size)
            {
                long into = (long)(rva - s.Rva);
                return Bytes.AsSpan(s.Offset + (int)into, (int)Math.Min(max, s.Size - into));
            }
        return default;
    }

    public ReadOnlySpan<byte> AtVa(ulong va, int max) => va < ImageBase ? default : AtRva(va - ImageBase, max);

    // a printable UTF-8 string that ends in a zero within max bytes
    public string? TextAtVa(ulong va, int max) => Text(AtVa(va, max + 1));

    private static string? Text(ReadOnlySpan<byte> span)
    {
        int end = span.IndexOf((byte)0);
        if (end < 0 || !Utf8.IsValid(span[..end])) return null;
        var s = Encoding.UTF8.GetString(span[..end]);
        foreach (var c in s)
            if (c < 0x20 && c is not ('\t' or '\r' or '\n') || c == 0x7f) return null;
        return s;
    }

    private Dictionary<string, uint> ReadExports()
    {
        var map = new Dictionary<string, uint>(StringComparer.Ordinal);
        var dir = AtRva(exportRva, 40);
        if (exportRva == 0 || dir.Length < 40) return map;
        uint functions = Read32(dir, 20), names = Read32(dir, 24), funcTable = Read32(dir, 28), nameTable = Read32(dir, 32), ordTable = Read32(dir, 36);
        if (names > functions) return map;
        for (uint i = 0; i < Math.Min(names, MaxExports); i++)
        {
            var np = AtRva(nameTable + 4UL * i, 4);
            var op = AtRva(ordTable + 2UL * i, 2);
            if (np.Length < 4 || op.Length < 2) break;
            uint ordinal = BinaryPrimitives.ReadUInt16LittleEndian(op);
            if (ordinal >= functions) continue;
            var fp = AtRva(funcTable + 4UL * ordinal, 4);
            if (fp.Length == 4 && Text(AtRva(Read32(np, 0), 257)) is { } name) map.TryAdd(name, Read32(fp, 0));
        }
        return map;
    }

    // RT_VERSION, its first name and first language, then VS_FIXEDFILEINFO's file version (what FileVersionInfo calls
    // FileMajorPart and friends)
    private Version? ReadVersion(uint rva)
    {
        int root = Offset(rva);
        if (root < 0) return null;
        uint? Entry(int dir, uint? id)
        {
            if (dir < 0 || dir + 16 > Bytes.Length) return null;
            int entries = Math.Min(U16(Bytes, dir + 12) + U16(Bytes, dir + 14), MaxResourceEntries);
            for (int i = 0; i < entries; i++)
            {
                if (dir + 24 + i * 8 > Bytes.Length) return null;
                if (id is null || U32(Bytes, dir + 16 + i * 8) == id) return U32(Bytes, dir + 20 + i * 8);
            }
            return null;
        }
        int? Sub(uint? entry) => entry is { } v && (v & SubdirectoryBit) != 0 ? root + (int)(v & ~SubdirectoryBit) : null;
        if (Sub(Entry(root, 16)) is not { } type || Sub(Entry(type, null)) is not { } lang) return null;
        if (Entry(lang, null) is not { } leaf || (leaf & SubdirectoryBit) != 0) return null;
        int dataEntry = root + (int)leaf;
        if (dataEntry < 0 || dataEntry + 8 > Bytes.Length) return null;
        int data = Offset(U32(Bytes, dataEntry));
        if (data < 0) return null;
        long end = Math.Min((long)data + U32(Bytes, dataEntry + 4), Bytes.Length);
        for (int i = data; i + 52 <= end; i += 4)
        {
            if (U32(Bytes, i) != 0xFEEF04BD) continue;
            uint ms = U32(Bytes, i + 8), ls = U32(Bytes, i + 12);
            return new Version((int)(ms >> 16), (int)(ms & 0xFFFF), (int)(ls >> 16), (int)(ls & 0xFFFF));
        }
        return null;
    }

    private int Offset(uint rva)
    {
        foreach (var s in sections)
            if (rva >= s.Rva && rva - s.Rva < s.Size) return s.Offset + (int)(rva - s.Rva);
        return -1;
    }

    internal static uint U32(byte[] d, long at) => at >= 0 && at + 4 <= d.Length ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan((int)at)) : 0;
    private static ushort U16(byte[] d, long at) => at >= 0 && at + 2 <= d.Length ? BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan((int)at)) : (ushort)0;
    internal static uint Read32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
}
