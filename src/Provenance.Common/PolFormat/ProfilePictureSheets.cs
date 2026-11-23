namespace Provenance.Common.PolFormat;

// 4x2 cells at 64x96, so profile picture F is sheet F/8, cell F%8
public static class ProfilePictureSheets
{
    public const int CellWidth = 64, CellHeight = 96, Columns = 4, Rows = 2, PerSheet = Columns * Rows;
    private const uint Mult = 0x19660d;
    private static readonly byte[] PngSig = [0x89, 0x50, 0x4e, 0x47];

    // an LCG XOR seeded by how far the first four bytes are off the PNG signature, a real PNG comes back as is
    public static byte[] Decode(byte[] data)
    {
        if (data.Length <= 4 || data.AsSpan(0, 4).SequenceEqual(PngSig)) return data;
        var d = (byte[])data.Clone();
        uint seed = 0;
        for (int i = 0; i < 4; i++)
        {
            byte sb = (byte)((d[i] - PngSig[i]) & 0xff);
            seed |= (uint)sb << (8 * i);
            d[i] = PngSig[i];
        }
        int p = 4;
        int pairs = (d.Length - 4) >> 1;
        for (int i = 0; i < pairs; i++)
        {
            d[p] ^= (byte)(seed >> 16);
            d[p + 1] ^= (byte)(seed >> 24);
            seed *= Mult;
            p += 2;
        }
        if (((d.Length - 4) & 1) != 0) d[p] ^= (byte)(seed >> 16);
        return d;
    }

    public static int SheetOf(int picId) => picId / PerSheet;

    public static int IdOf(int sheet, int cell) => sheet * PerSheet + cell;

    public static string? SheetPath(string viewerDir, int sheet)
    {
        if (sheet < 0) return null;
        var path = Path.Combine(viewerDir, "data", "icon", "download", $"hnf{sheet:D3}.png");
        return File.Exists(path) ? path : null;
    }

    public static string? SheetPath(string viewerDir, string? extrasDir, int sheet) =>
        SheetPath(viewerDir, sheet) ?? (extrasDir is null ? null : SheetPath(extrasDir, sheet));

    public static (int X, int Y, int Width, int Height) Cell(int picId)
    {
        int c = picId % PerSheet;
        return ((c % Columns) * CellWidth, ((c / Columns) % Rows) * CellHeight, CellWidth, CellHeight);
    }
}
