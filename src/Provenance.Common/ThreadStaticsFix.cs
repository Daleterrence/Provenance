namespace Provenance.Common;

// .NET 10.0.14 on x86 dies when a thread static fits its 48 byte direct TLS area but not once it's aligned (the tray
// menu hit it). Filling the area a byte at a time first leaves no gap to fall into, drop it once a fixed runtime ships
public static class ThreadStaticsFix
{
    private const int DirectAreaBytes = 48;

    public static void Apply() => Fill<Root>(DirectAreaBytes);

    private static void Fill<T>(int left)
    {
        OneByte<T>.Touched = 1;
        if (left > 1) Fill<OneByte<T>>(left - 1);
    }

    private sealed class Root;

    private sealed class OneByte<T>
    {
        [ThreadStatic] internal static byte Touched;
    }
}
