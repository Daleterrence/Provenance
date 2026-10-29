using System.IO.Compression;

namespace Provenance.Common.Patcher;

// app.dll's file worker at 0x102aa101. The bit codecs start with a u32 bit count, then the bits low first.
public static class PatchCodecs
{
    // .slc type byte: 1 = stored, 3 = zlib, anything else bit-LZSS
    public static void DecodeSlc(byte type, string tmp, string output, long maxLength, CancellationToken ct)
    {
        using var src = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var dst = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        if (type == 3)
        {
            using var zlib = new ZLibStream(src, CompressionMode.Decompress);
            CappedCopy(zlib, dst, maxLength, ct);
        }
        else if (type == 1) CappedCopy(src, dst, maxLength, ct);
        else Lzss(src, dst, ct, maxLength);
    }

    private static void CappedCopy(Stream src, Stream dst, long maxLength, CancellationToken ct)
    {
        var buf = new byte[1 << 20];
        long total = 0;
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if ((total += n) > maxLength) throw new InvalidDataException("The update data is larger than the file it should make.");
            dst.Write(buf, 0, n);
        }
    }

    // app.dll 0x102ae1e5: flag 0 = an 8-bit literal, flag 1 = 16-bit distance + 8-bit length copied from the output
    public static void Lzss(Stream src, Stream dst, CancellationToken ct, long maxLength = long.MaxValue) =>
        Decode(new BitReader(src), new Window(dst, maxLength), null, ct);

    // app.dll 0x102ae777, the same but a 1 flag is followed by 0 for the output copy, or 1 + signed 16-bit offset +
    // 8-bit length to copy from the base at (output position + offset)
    public static void ApplyOlc(string delta, string baseFile, string output, long maxLength, CancellationToken ct)
    {
        using var src = new FileStream(delta, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var bas = new FileStream(baseFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var dst = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        Decode(new BitReader(src), new Window(dst, maxLength), bas, ct);
    }

    private static void Decode(BitReader bits, Window win, Stream? bas, CancellationToken ct)
    {
        var chunk = new byte[256];
        while (bits.More)
        {
            if (bits.Read(1) == 0) win.Put((byte)bits.Read(8));
            else if (bas == null || bits.Read(1) == 0)
            {
                int dist = (int)bits.Read(16), len = (int)bits.Read(8);
                win.CopyBack(dist, len);
            }
            else
            {
                int off = (short)bits.Read(16), len = (int)bits.Read(8);
                long at = win.Position + off;
                if (at < 0 || at + len > bas.Length) throw new InvalidDataException("The update delta points outside the old file.");
                bas.Position = at;
                bas.ReadExactly(chunk, 0, len);
                for (int i = 0; i < len; i++) win.Put(chunk[i]);
            }
            if ((win.Position & 0xFFFFF) == 0) ct.ThrowIfCancellationRequested();
        }
        win.Flush();
    }

    private sealed class BitReader(Stream s)
    {
        private readonly long _total = new BinaryReader(s).ReadUInt32();
        private long _pos;
        private int _cur, _left;

        public bool More => _pos < _total;

        public uint Read(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++)
            {
                if (_left == 0)
                {
                    _cur = s.ReadByte();
                    if (_cur < 0) throw new InvalidDataException("The update data ended early.");
                    _left = 8;
                }
                v |= (uint)(_cur & 1) << i;
                _cur >>= 1;
                _left--;
            }
            _pos += n;
            return v;
        }
    }

    private sealed class Window(Stream dst, long maxLength)
    {
        private const int Size = 0x10000, Mask = Size - 1;
        private readonly byte[] _ring = new byte[Size];
        private readonly byte[] _out = new byte[1 << 16];
        private int _fill;
        public long Position { get; private set; }

        public void Put(byte b)
        {
            if (Position >= maxLength) throw new InvalidDataException("The update data is larger than the file it should make.");
            _ring[Position & Mask] = b;
            Position++;
            _out[_fill++] = b;
            if (_fill == _out.Length) Flush();
        }

        public void CopyBack(int dist, int len)
        {
            if (dist <= 0 || dist > Position || dist > Size) throw new InvalidDataException("The update data refers back too far.");
            for (int i = 0; i < len; i++) Put(_ring[(Position - dist) & Mask]);
        }

        public void Flush()
        {
            dst.Write(_out, 0, _fill);
            _fill = 0;
        }
    }
}
