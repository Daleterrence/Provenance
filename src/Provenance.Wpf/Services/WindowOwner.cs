#pragma warning disable RS0030
using System.Windows;

namespace Provenance.Wpf.Services;

internal static class WindowOwner
{
    internal static Func<Window?>? ActiveSeam { get; set; }

    public static Window? Active() => ActiveSeam is { } seam ? seam() : Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

    public static Window? Find() => Find(Active());

    public static Window? Find(Window? active) => active ?? (Application.Current?.MainWindow is { IsVisible: true } main ? main : null);

    // the app ends with its main window, and WPF then closes whatever's left without waiting on its Closing
    public static bool AppClosing => Application.Current?.MainWindow is null;
}
