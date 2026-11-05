using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Provenance.Common.Social;
using Serilog;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Provenance.Wpf.Services;

public enum IconState { Idle, Selected, Transition, Active }

// the SVG icons built in from the assets folder, drawn as vectors so they stay sharp at any scale
public static partial class SvgIcons
{
    public static readonly string[] Glyphs =
    [
        "card", "smiley", "chat", "key", "groupchat2", "search", "wave", "bubble", "ring", "list", "sword", "person",
        "delete", "add", "copy", "help", "ok", "lock", "star", "edit", "mail", "close", "dismiss", "folder",
        "left", "right", "up", "down", "settings", "accounts", "more", "download", "verified", "plugin", "gamepad", "profile-edit",
    ];
    public static readonly string[] OnlineKeys = ["online", "away", "invisible"];

    private const string Root = "pack://application:,,,/Provenance;component/Assets/icons/";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DrawingImage?> Cache = new();
    private static readonly Dictionary<string, Rect> GlyphFrames = new();
    private static readonly Dictionary<string, (Vector[] Shift, Rect Frame)> ButtonFits = new();

    // every file there should be, for the tests
    public static IEnumerable<string> Files =>
        Enumerable.Range(1, ChatLook.Emotes).Select(EmoteFile)
            .Concat(Glyphs.SelectMany(g => new[] { $"status-{g}.svg" }.Concat(Enum.GetValues<IconState>().Select(s => GlyphFile(g, s)))))
            .Concat(OnlineKeys.Select(k => $"online-{k}.svg"))
            .Append(XiBadgeFile)
            .Append(NoticeFile);

    private const string XiBadgeFile = "xi-badge.svg", NoticeFile = "notice.svg";
    private static string EmoteFile(int i) => $"chat_face-{i:00}.svg";
    // POL's files are named the other way round: its blue icon (-open) is the selected look and its green icon (-hover)
    // is the one for a button that's in use
    internal static string GlyphFile(string glyph, IconState state) => $"status-{glyph}-{state switch
    {
        IconState.Selected => "open",
        IconState.Transition => "transition",
        IconState.Active => "hover",
        _ => "idle",
    }}.svg";

    public static ImageSource? Glyph(string glyph, IconState state) =>
        Cached(GlyphFile(glyph, state), () => Frame(Load(GlyphFile(glyph, state)), GlyphFrame(glyph)));

    // the single-colour file in one colour, no disc, for small marks and anything sat on a dark surface where the idle
    // icon's black outline gets lost
    public static ImageSource? Tinted(string glyph, Color colour) =>
        Cached($"status-{glyph}.svg#{colour}", () => Frame(Load($"status-{glyph}.svg", colour), GlyphFrame(glyph)));

    // for a button, which draws its own disc: POL's green or blue disc comes out and the outline goes back to idle. POL
    // nudges its icons a pixel per state, so each gets centred instead
    public static ImageSource? ButtonGlyph(string glyph, IconState state) =>
        Cached(GlyphFile(glyph, state) + "#button", () =>
        {
            if (ButtonArt(glyph, state) is not { } art) return null;
            var (shift, frame) = ButtonFit(glyph);
            var moved = new DrawingGroup { Transform = new TranslateTransform(shift[(int)state].X, shift[(int)state].Y) };
            moved.Children.Add(art);
            return Frame(moved, frame);
        });

    // on a button every glyph draws at one scale, so the discs (r 10 round 16,16) all match. One that pokes further out
    // of its disc spills past its box instead of shrinking
    public const double ButtonBox = 20;
    public const double DiscShare = 20 / ButtonBox;
    public static double ButtonScale(string glyph) => ButtonFit(glyph).Frame.Width / ButtonBox;
    internal static Rect FrameOf(string glyph) => ButtonFit(glyph).Frame;

    private static Drawing? ButtonArt(string glyph, IconState state) =>
        Load(GlyphFile(glyph, state), edit: state is IconState.Selected or IconState.Active ? svg => OnButton(glyph, svg) : null);

    private static (Vector[] Shift, Rect Frame) ButtonFit(string glyph)
    {
        lock (Gate)
        {
            if (ButtonFits.TryGetValue(glyph, out var have)) return have;
            const double centre = 16;
            double half = 10;
            var shifts = new Vector[4];
            foreach (var state in Enum.GetValues<IconState>())
            {
                if (ButtonArt(glyph, state) is not { } art || Ink(art) is not { IsEmpty: false } ink) continue;
                var shift = new Vector(centre - (ink.Left + ink.Right) / 2, centre - (ink.Top + ink.Bottom) / 2);
                shifts[(int)state] = shift;
                // a bit over, so the frame's clip never shaves the soft edge
                half = Math.Max(half, Math.Max(ink.Width, ink.Height) / 2 + 0.5);
            }
            var fit = (shifts, new Rect(centre - half, centre - half, half * 2, half * 2));
            ButtonFits[glyph] = fit;
            return fit;
        }
    }

    // where the icon really puts ink, off a render at 8x. Bounds counts clipped and see-through bits as well
    internal static Rect Ink(Drawing drawing)
    {
        const int scale = 8, from = -8, units = 48, size = units * scale;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, -from * scale, -from * scale));
            dc.DrawDrawing(drawing);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var px = new byte[size * size * 4];
        bitmap.CopyPixels(px, size * 4, 0);
        int left = size, top = size, right = -1, bottom = -1;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                if (px[(y * size + x) * 4 + 3] > 24)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
        return right < 0 ? Rect.Empty : new Rect((double)left / scale + from, (double)top / scale + from, (right - left + 1.0) / scale, (bottom - top + 1.0) / scale);
    }

    private const string HoverDisc = "#7CCB96", OpenDisc = "#7DC2F1";

    internal static string OnButton(string glyph, string svg)
    {
        svg = DiscCircle().Replace(svg, "");
        // the dark colours line up darkest to darkest with the idle file's, anything near white goes white
        static List<string> Darks(string text) => ColourAttr().Matches(text).Select(m => m.Groups[2].Value.ToUpperInvariant())
            .Where(c => c != HoverDisc && c != OpenDisc && Light(c) < 0.8).Distinct().OrderBy(Light).ToList();
        var idle = Text(GlyphFile(glyph, IconState.Idle)) is { } t ? Darks(t) : [];
        var mine = Darks(svg);
        return ColourAttr().Replace(svg, m =>
        {
            var c = m.Groups[2].Value.ToUpperInvariant();
            if (Light(c) >= 0.8) return m.Groups[1].Value + "#FFFFFF\"";
            int i = mine.IndexOf(c);
            return i < 0 || idle.Count == 0 ? m.Value : m.Groups[1].Value + idle[Math.Min(i, idle.Count - 1)] + "\"";
        });
    }

    private static double Light(string hex)
    {
        int v = Convert.ToInt32(hex[1..], 16);
        return (0.2126 * (v >> 16 & 255) + 0.7152 * (v >> 8 & 255) + 0.0722 * (v & 255)) / 255;
    }

    public static ImageSource? Emote(int index) =>
        index is >= 1 and <= ChatLook.Emotes ? Cached(EmoteFile(index), () => Framed(EmoteFile(index))) : null;

    // busy shows as online and Appear offline as invisible, same as POL. Offline has no badge. Unfilled loses the pale
    // disc inside the ring, for the statuses the picker isn't on
    public static ImageSource? Online(string key, bool filled = true)
    {
        if (key switch { "online" or "busy" => "online-online.svg", "away" => "online-away.svg", "invisible" => "online-invisible.svg", _ => null }
            is not { } file)
            return null;
        return filled ? Cached(file, () => Framed(file))
            : Cached(file + "#unfilled", () => Frame(Load(file, edit: Unfilled), ViewBox(file)));
    }

    // the ring's a full disc under the pale one, so it becomes a band with the pale disc's hole cut out of it
    internal static string Unfilled(string svg) => RingAndInside().Replace(svg, m =>
    {
        static string Circle(double x, double y, double r) => FormattableString.Invariant($"M{x - r},{y}A{r},{r} 0 1 0 {x + r},{y}A{r},{r} 0 1 0 {x - r},{y}Z");
        double N(string g) => double.Parse(m.Groups[g].Value, System.Globalization.CultureInfo.InvariantCulture);
        return $"<path d=\"{Circle(N("x"), N("y"), N("r"))}{Circle(N("x"), N("y"), N("hole"))}\" fill-rule=\"evenodd\" fill=\"url(#{m.Groups["ring"].Value})\"/>";
    });

    public static ImageSource? XiBadge => Cached(XiBadgeFile, () => Framed(XiBadgeFile));

    // POL's exclamation mark, for errors and notices
    public static ImageSource? Notice => Cached(NoticeFile, () => Framed(NoticeFile));

    private static DrawingImage? Cached(string key, Func<Drawing?> make)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var have)) return have;
            DrawingImage? img = null;
            if (make() is { } drawing)
            {
                img = new DrawingImage(drawing);
                img.Freeze();
            }
            Cache[key] = img;
            return img;
        }
    }

    private static Drawing? Framed(string file) => Frame(Load(file), ViewBox(file));

    // the image is exactly the frame, so the drawing sits where the file put it and nothing outside it shows
    private static Drawing? Frame(Drawing? drawing, Rect? frame)
    {
        if (drawing is null || frame is not { } f) return drawing;
        var group = new DrawingGroup { ClipGeometry = new RectangleGeometry(f) };
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(f)));
        group.Children.Add(drawing);
        return group;
    }

    // one square for all four states round the badge's centre, so the glyph never shifts when the state changes. Some
    // states poke out of the disc, so it's the biggest of them
    private static Rect? GlyphFrame(string glyph)
    {
        lock (Gate)
        {
            if (GlyphFrames.TryGetValue(glyph, out var have)) return have;
            const double centre = 16;
            double half = 10;
            foreach (var state in Enum.GetValues<IconState>())
                if (Load(GlyphFile(glyph, state)) is { Bounds: { IsEmpty: false } b })
                    half = Math.Max(half, new[] { centre - b.Left, b.Right - centre, centre - b.Top, b.Bottom - centre }.Max());
            var frame = new Rect(centre - half, centre - half, half * 2, half * 2);
            GlyphFrames[glyph] = frame;
            return frame;
        }
    }

    // the single-colour files say currentColor, which gets swapped for a real one before the renderer sees it
    internal static Drawing? Load(string file, Color? currentColour = null, Func<string, string>? edit = null)
    {
        try
        {
            if (Text(file) is not { } svg) return null;
            if (currentColour is { } c) svg = svg.Replace("currentColor", $"#{c.R:X2}{c.G:X2}{c.B:X2}", StringComparison.Ordinal);
            if (edit is not null) svg = edit(svg);
            // left to itself the renderer shifts a drawing so its top left sits on the viewBox's, and search has a
            // (clipped) rectangle out at -8 that it then shoves 8 units down and right
            var settings = new WpfDrawingSettings
            {
                IncludeRuntime = false,
                TextAsGeometry = true,
                EnsureViewboxPosition = false,
                EnsureViewboxSize = false,
            };
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
            var drawing = new FileSvgReader(settings).Read(stream);
            drawing?.Freeze();
            return drawing;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Icon {File} didn't load", file);
            return null;
        }
    }

    private static Rect? ViewBox(string file)
    {
        if (Text(file) is not { } svg || ViewBoxAttr().Match(svg) is not { Success: true } m) return null;
        var parts = m.Groups[1].Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var n = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n[i]))
                return null;
        return n.Length == 4 && n[2] > 0 && n[3] > 0 ? new Rect(n[0], n[1], n[2], n[3]) : null;
    }

    private static string? Text(string file)
    {
        try
        {
            var info = Application.GetResourceStream(new Uri(Root + file));
            if (info is null) return null;
            using var reader = new StreamReader(info.Stream);
            return reader.ReadToEnd();
        }
        catch (IOException) { return null; }
    }

    [GeneratedRegex(@"<svg\b[^>]*\bviewBox=""([^""]+)""")]
    private static partial Regex ViewBoxAttr();

    [GeneratedRegex(@"<circle cx=""16"" cy=""16"" r=""10"" fill=""#(?:7CCB96|7DC2F1)""\s*/>", RegexOptions.IgnoreCase)]
    private static partial Regex DiscCircle();

    [GeneratedRegex(@"((?:fill|stroke|stop-color)="")(#[0-9A-Fa-f]{6})""")]
    private static partial Regex ColourAttr();

    [GeneratedRegex(@"<circle cx=""(?<x>[\d.]+)"" cy=""(?<y>[\d.]+)"" r=""(?<r>[\d.]+)"" fill=""url\(#(?<ring>[\w-]+-ring)\)""\s*/>\s*<circle cx=""\k<x>"" cy=""\k<y>"" r=""(?<hole>[\d.]+)"" fill=""url\(#[\w-]+-in\)""\s*/>")]
    private static partial Regex RingAndInside();
}
