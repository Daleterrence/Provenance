#pragma warning disable RS0030 // app-level lookups live in the services
using System.Windows;

namespace Provenance.Wpf.Services;

internal static class WindowOwner
{
    public static Window? Active() => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

    // the active window, else the main one if it's up
    public static Window? Find() => Find(Active());

    public static Window? Find(Window? active) => active ?? (Application.Current?.MainWindow is { IsVisible: true } main ? main : null);
}
