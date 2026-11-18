namespace Provenance.Wpf.Services;

// Show timestamps flips for the windows that are already open, which hear it here
public static class Timestamps
{
    public static event Action? Changed;

    internal static void RaiseChanged() => Changed?.Invoke();
}
