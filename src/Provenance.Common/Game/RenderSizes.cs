namespace Provenance.Common.Game;

public static class RenderSizes
{
    public const int Max = 4096;

    public static readonly int[] Squares = [1024, 1280, 1440, 1600, 1680, 1920, 2048, 2560, 2880, 3200, 3440, 3840, 4096];

    public static int ClosestTo(int width) => Squares.MinBy(s => (Math.Abs(s - width), -s));
}
