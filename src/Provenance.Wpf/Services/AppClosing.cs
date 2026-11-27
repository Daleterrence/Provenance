using Serilog;

namespace Provenance.Wpf.Services;

internal static class AppClosing
{
    public static void Because(string why) => Log.Information("Closing Provenance: {Why:l}", why);
}
