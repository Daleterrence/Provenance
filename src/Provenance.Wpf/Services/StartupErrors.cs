using System.Windows;
using Serilog;

namespace Provenance.Wpf.Services;

public static class StartupErrors
{
    public static void Show(string title, string text, bool offerLogs, string? boxText = null)
    {
        try { WpfDialogs.Default.Error(title, text, offerLogs); }
        catch (Exception ex)
        {
            Log.Error(ex, "The start-up error card could not be shown");
            #pragma warning disable RS0030
            MessageBox.Show(boxText ?? text, "Provenance", MessageBoxButton.OK, MessageBoxImage.Error);
            #pragma warning restore RS0030
        }
    }
}
