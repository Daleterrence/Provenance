using System.IO;
using System.Windows.Media.Imaging;
using Provenance.Wpf.Win32;

namespace Provenance.Wpf.Services;

internal static class BadgePictures
{
    private static readonly int[] OverlaySizes = [16, 20, 24, 32, 40, 48];

    private static readonly Dictionary<string, BitmapFrame> Overlays = [];

    private static readonly System.Drawing.Color Red = System.Drawing.Color.FromArgb(237, 66, 69);

    internal static BitmapFrame Overlay(string label)
    {
        lock (Overlays)
        {
            if (Overlays.TryGetValue(label, out var have)) return have;
            var frames = OverlaySizes.Select(n =>
            {
                using var bmp = new System.Drawing.Bitmap(n, n, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Smooth(bmp)) Badge(g, 0, 0, n, label);
                using var png = new MemoryStream();
                bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                return (n, png: png.ToArray());
            }).ToList();
            var made = Decode(Icon(frames)).Frames[0];
            Overlays[label] = made;
            return made;
        }
    }

    internal static System.Drawing.Icon Tray(System.Drawing.Icon app, string label)
    {
        int n = app.Width;
        using var bmp = new System.Drawing.Bitmap(n, n, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Smooth(bmp))
        {
            using var original = app.ToBitmap();
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.None;
            g.DrawImage(original, 0, 0, n, n);
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            int d = (int)Math.Round(n * 0.66);
            Badge(g, n - d, n - d, d, label);
        }
        var handle = bmp.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { User32.DestroyIcon(handle); }
    }

    internal static System.Drawing.Bitmap Drawn(int size, string label)
    {
        var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Smooth(bmp);
        Badge(g, 0, 0, size, label);
        return bmp;
    }

    private static System.Drawing.Graphics Smooth(System.Drawing.Bitmap bmp)
    {
        var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        return g;
    }

    private static void Badge(System.Drawing.Graphics g, float x, float y, float d, string label)
    {
        using var red = new System.Drawing.SolidBrush(Red);
        g.FillEllipse(red, x, y, d, d);
        if (label.Length == 0) return;
        float em = label.Length == 1 ? d * 0.70f : d * 0.56f;
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        using var family = new System.Drawing.FontFamily("Segoe UI");
        path.AddString(label, family, (int)System.Drawing.FontStyle.Bold, em, new System.Drawing.PointF(0, 0), System.Drawing.StringFormat.GenericTypographic);
        var b = path.GetBounds();
        using var move = new System.Drawing.Drawing2D.Matrix();
        move.Translate(x + d / 2 - (b.Left + b.Width / 2), y + d / 2 - (b.Top + b.Height / 2));
        path.Transform(move);
        g.FillPath(System.Drawing.Brushes.White, path);
    }

    private static byte[] Icon(List<(int n, byte[] png)> frames)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var (n, png) in frames)
        {
            w.Write((byte)n);
            w.Write((byte)n);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in frames) w.Write(png);
        return ms.ToArray();
    }

    private static IconBitmapDecoder Decode(byte[] ico) =>
        new(new MemoryStream(ico), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
}
