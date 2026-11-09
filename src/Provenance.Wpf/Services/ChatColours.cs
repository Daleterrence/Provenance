using System.Windows;
using System.Windows.Media;

namespace Provenance.Wpf.Services;

internal static class ChatColours
{
    internal static SolidColorBrush LineBrush(uint argb) => LineBrush(argb, Grounds());

    // what the lines and the colour picker sit on, in whichever theme's showing
    internal static Color[] Grounds() =>
        [.. new[] { "Bg.Base", "Surface.1", "Surface.2" }
            .Select(k => Application.Current?.TryFindResource(k) as SolidColorBrush).OfType<SolidColorBrush>().Select(b => b.Color)];

    // the viewer's colours are for its light window. On a dark one they're lifted to the same hue at a lightness that
    // reads, on a light one only the few too pale get darkened
    internal static SolidColorBrush LineBrush(uint argb, IReadOnlyList<Color> grounds)
    {
        bool light = grounds.Count > 0 && grounds.Average(c => (c.R + c.G + c.B) / 765.0) > 0.5;
        double r = ((argb >> 16) & 0xff) / 255.0, g = ((argb >> 8) & 0xff) / 255.0, b = (argb & 0xff) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, h = 0, s = 0;
        if (max > min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h /= 6;
        }
        if (!light)
        {
            l = Math.Max(l, 0.68);
            s = Math.Min(1, s * 1.1);
        }
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        Color Rgb(double l)
        {
            byte C(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
            if (s == 0) return Color.FromRgb(C(l), C(l), C(l));
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            return Color.FromRgb(C(Hue(p, q, h + 1 / 3.0)), C(Hue(p, q, h)), C(Hue(p, q, h - 1 / 3.0)));
        }
        // HSL's lightness isn't what the eye sees (a yellow at 0.4 is still pale, a blue at 0.7 still dark), so it
        // carries on till it's AA on everything it sits on
        var colour = Rgb(l);
        double Worst(Color c) => grounds.Count == 0 ? 21 : grounds.Min(gr => Contrast(c, gr));
        if (light) while (Worst(colour) < 4.5 && l > 0) colour = Rgb(l -= 0.02);
        else while (Worst(colour) < 4.5 && l < 1) colour = Rgb(l += 0.02);
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    internal static double Contrast(Color a, Color b)
    {
        static double Lum(Color c)
        {
            static double Ch(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        double la = Lum(a), lb = Lum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
