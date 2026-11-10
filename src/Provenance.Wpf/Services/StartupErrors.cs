using System.Windows;
using Serilog;

namespace Provenance.Wpf.Services;

public static class StartupErrors
{
    // the card needs the app's windows, so if that's what broke it falls back to a plain message box
    public static void Show(string title, string text, bool offerLogs, string? boxText = null)
    {
        try { WpfDialogs.Default.Error(title, text, offerLogs); }
        catch (Exception ex)
        {
            Log.Error(ex, "The start-up error card could not be shown");
            #pragma warning disable RS0030 // the app's dialogs are what just failed
            MessageBox.Show(boxText ?? text, "Provenance", MessageBoxButton.OK, MessageBoxImage.Error);
            #pragma warning restore RS0030
        }
    }
}
