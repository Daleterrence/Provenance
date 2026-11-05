using System.Buffers.Binary;
using System.Text;

namespace Provenance.Common.Game;

// reads a DLL's export names and version straight off the disk. Nothing gets loaded, not even as a datafile, so a
// plugin's code never runs
internal sealed class PeImage
{
    public const ushort MachineI386 = 0x014C;
    // a plugin's a few MB at most. 96 sections is the loader's own limit, and the other caps keep a crafted file cheap
    private const long MaxFile = 16L << 20;
    private const int MaxSections = 96, MaxExports = 16384, MaxResourceEntries = 4096;
    private const uint SubdirectoryBit = 0x80000000;

    public ushort Machine { get; private set; }
    public IReadOnlyList<string> Exports { get; private set; } = [];
    public Version? FileVersion { get; private set; }

    private readonly byte[] _d;
    private readonly (uint Va, uint Size, uint Raw, uint RawSize)[] _sections;

    private PeImage(byte[] d, (uint, uint, uint, uint)[] sections)
    {
        _d = d;
        _sections = sections;
    }

    public static Version? ReadFileVersion(string path) => Read(path)?.FileVersion;

    public static PeImage? Read(string path)
    {
        byte[] d;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxFile) return null;
            d = new byte[fs.Length];
            fs.ReadExactly(d);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        return Parse(d);
    }

    // every offset comes from the file, so anything out of bounds just means it isn't one
    internal static PeImage? Parse(byte[] d)
    {
        try
        {
            if (d.Length < 0x40 || d[0] != 'M' || d[1] != 'Z') return null;
            int pe = checked((int)U32(d, 0x3C));
            if (U32(d, pe) != 0x00004550) return null;
            int count = U16(d, pe + 6), optSize = U16(d, pe + 20), opt = pe + 24;
            if (count > MaxSections) return null;
            int dirs = U16(d, opt) switch { 0x10B => opt + 96, 0x20B => opt + 112, _ => -1 };
            if (dirs < 0) return null;
            uint dirCount = U32(d, dirs - 4);
            var sections = new (uint, uint, uint, uint)[count];
            for (int i = 0; i < count; i++)
            {
                int s = opt + optSize + i * 40;
                sections[i] = (U32(d, s + 12), U32(d, s + 8), U32(d, s + 20), U32(d, s + 16));
            }
            var img = new PeImage(d, sections) { Machine = U16(d, pe + 4) };
            if (dirCount > 0 && U32(d, dirs) is var exports and not 0) img.Exports = img.ExportNames(exports);
            if (dirCount > 2 && U32(d, dirs + 16) is var resources and not 0) img.FileVersion = img.VersionResource(resources);
            return img;
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or OverflowException) { return null; }
    }

    private int Offset(uint rva)
    {
        foreach (var (va, size, raw, rawSize) in _sections)
            if (rva >= va && rva - va < Math.Max(size, rawSize))
                return rva - va < rawSize ? checked((int)(raw + (rva - va))) : -1;
        return -1;
    }

    private List<string> ExportNames(uint rva)
    {
        var names = new List<string>();
        int e = Offset(rva);
        int table = e < 0 ? -1 : Offset(U32(_d, e + 32));
        if (table < 0) return names;
        uint n = Math.Min(U32(_d, e + 24), MaxExports);
        for (int i = 0; i < n; i++)
        {
            int at = Offset(U32(_d, table + i * 4));
            if (at < 0) continue;
            int end = Array.IndexOf(_d, (byte)0, at, Math.Min(512, _d.Length - at));
            if (end > at) names.Add(Encoding.ASCII.GetString(_d, at, end - at));
        }
        return names;
    }

    // RT_VERSION, its first name and first language, then VS_FIXEDFILEINFO's file version (what FileVersionInfo calls
    // FileMajorPart and friends)
    private Version? VersionResource(uint rva)
    {
        int root = Offset(rva);
        if (root < 0) return null;
        uint? Entry(int dir, uint? id)
        {
            int entries = Math.Min(U16(_d, dir + 12) + U16(_d, dir + 14), MaxResourceEntries);
            for (int i = 0; i < entries; i++)
                if (id is null || U32(_d, dir + 16 + i * 8) == id) return U32(_d, dir + 20 + i * 8);
            return null;
        }
        int? Sub(uint? entry) => entry is { } v && (v & SubdirectoryBit) != 0 ? root + (int)(v & ~SubdirectoryBit) : null;
        if (Sub(Entry(root, 16)) is not { } type || Sub(Entry(type, null)) is not { } lang) return null;
        if (Entry(lang, null) is not { } leaf || (leaf & SubdirectoryBit) != 0) return null;
        int dataEntry = root + (int)leaf;
        int data = Offset(U32(_d, dataEntry));
        if (data < 0) return null;
        long end = Math.Min((long)data + U32(_d, dataEntry + 4), _d.Length);
        for (int i = data; i + 52 <= end; i += 4)
        {
            if (U32(_d, i) != 0xFEEF04BD) continue;
            uint ms = U32(_d, i + 8), ls = U32(_d, i + 12);
            return new Version((int)(ms >> 16), (int)(ms & 0xFFFF), (int)(ls >> 16), (int)(ls & 0xFFFF));
        }
        return null;
    }

    private static uint U32(byte[] d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at, 4));
    private static ushort U16(byte[] d, int at) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at, 2));
}
