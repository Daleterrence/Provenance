namespace Provenance.Common.Social;

public readonly record struct Spot(double Left, double Top, double Width, double Height);

public static class PopOutPlacement
{
    public const double Nudge = 28;

    private const double Same = 6;

    public static bool Usable(Spot s) =>
        double.IsFinite(s.Left) && double.IsFinite(s.Top) && double.IsFinite(s.Width) && double.IsFinite(s.Height) && s.Width > 0 && s.Height > 0;

    public static Spot Clamp(Spot want, Spot room, double minWidth, double minHeight)
    {
        double w = Math.Min(Math.Max(want.Width, minWidth), Math.Max(room.Width, minWidth));
        double h = Math.Min(Math.Max(want.Height, minHeight), Math.Max(room.Height, minHeight));
        double left = Math.Max(room.Left, Math.Min(want.Left, room.Left + room.Width - w));
        double top = Math.Max(room.Top, Math.Min(want.Top, room.Top + room.Height - h));
        return new Spot(left, top, w, h);
    }

    public static Spot Open(Spot saved, IEnumerable<(double Left, double Top)> taken, Spot room, double minWidth, double minHeight)
    {
        var at = Clamp(saved, room, minWidth, minHeight);
        var others = taken.ToList();
        for (int tries = 0; tries < 60 && others.Any(o => Math.Abs(o.Left - at.Left) < Same && Math.Abs(o.Top - at.Top) < Same); tries++)
        {
            double left = at.Left + Nudge, top = at.Top + Nudge;
            if (left + at.Width > room.Left + room.Width || top + at.Height > room.Top + room.Height) (left, top) = (room.Left, room.Top);
            at = at with { Left = left, Top = top };
        }
        return at;
    }
}
