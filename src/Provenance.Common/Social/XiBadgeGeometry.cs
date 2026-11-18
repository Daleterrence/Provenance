namespace Provenance.Common.Social;

// xi-badge.svg is 40 x 20, body at 1,1 (36 x 16, corners 4.2). The body runs further left with its flat top level with
// the circle's, so the rounded end hides behind the picture drawn over it
public static class XiBadgeGeometry
{
    public const double ViewWidth = 40, ViewHeight = 20, BodyTop = 1, Corner = 4.2,
                        LettersLeft = 18.92;

    public const double WidthOfDiameter = 0.64, LeftOfDiameter = 0.62;

    private const double Unit = WidthOfDiameter / ViewWidth;

    // where the longer body starts, in the file's own units
    public const double FrameLeft = (0.5 - Corner * Unit - LeftOfDiameter) / Unit;

    public const double FrameWidth = ViewWidth - FrameLeft, FrameBodyLeft = 0;

    public readonly record struct Layout(double Left, double Top, double Width, double Height);

    public static Layout For(double diameter)
    {
        double unit = diameter * Unit;
        return new Layout(diameter / 2 - Corner * unit, -BodyTop * unit, FrameWidth * unit, ViewHeight * unit);
    }
}
