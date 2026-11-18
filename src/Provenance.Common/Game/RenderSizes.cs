namespace Provenance.Common.Game;

// the 3D resolutions the settings page offers, all square and none past 4096
public static class RenderSizes
{
    public const int Max = 4096;

    public static readonly int[] Squares = [1024, 1280, 1440, 1600, 1680, 1920, 2048, 2560, 2880, 3200, 3440, 3840, 4096];

    // the nearest to the screen's width, the bigger one when it's halfway
    public static int ClosestTo(int width) => Squares.MinBy(s => (Math.Abs(s - width), -s));
}
