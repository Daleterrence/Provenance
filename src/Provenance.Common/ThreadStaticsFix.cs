namespace Provenance.Common;

// .NET 10.0.14 on x86 dies on a thread static that fits its 48 byte TLS area till aligned (the tray menu hit it).
// Filling it first leaves no gap, drop this once it's fixed
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
