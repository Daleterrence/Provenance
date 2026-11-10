namespace Provenance.Common.Game.Windower;

public readonly record struct WindowerSizes(int Width, int Height, int UIWidth, int UIHeight, float UIScale)
{
    public static WindowerSizes For(float uiScale, int width, int height, Func<(int Width, int Height)>? display = null)
    {
        if (width == 0 || height == 0) (width, height) = (display ?? Display.PrimaryAsWindowerSeesIt)();
        int uw = Truncate(width, uiScale), uh = Truncate(height, uiScale);
        return uw <= 0 || uh <= 0 ? new(width, height, width, height, 1f) : new(width, height, uw, uh, uiScale);
    }

    // .NET Framework's x86 JIT divides in double and truncates without rounding to float first, so 1100 / 1.1 is 999, not 1000.
    // Out of range or NaN gives 0x80000000
    internal static int Truncate(int size, float scale)
    {
        double q = size / (double)scale;
        return double.IsNaN(q) || q >= 2147483648.0 || q <= -2147483649.0 ? int.MinValue : (int)q;
    }
}
