#pragma warning disable RS0030
using System.Windows;

namespace Provenance.Wpf.Services;

internal static class WindowOwner
{
    // whether Windows has given a window the foreground depends on the desktop, so tests say which one's in front
    internal static Func<Window?>? ActiveSeam { get; set; }

    public static Window? Active() => ActiveSeam is { } seam ? seam() : Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

    // the active window, else the main one if it's up
    public static Window? Find() => Find(Active());

    public static Window? Find(Window? active) => active ?? (Application.Current?.MainWindow is { IsVisible: true } main ? main : null);

    // the app ends with its main window, and WPF then closes whatever's left without waiting on its Closing
    public static bool AppClosing => Application.Current?.MainWindow is null;
}
