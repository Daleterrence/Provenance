namespace Provenance.Wpf.Services;

public static class Timestamps
{
    public static event Action? Changed;

    internal static void RaiseChanged() => Changed?.Invoke();
}
