using System.Globalization;

namespace Provenance.Common;

public static class AppCulture
{
    private static readonly CultureInfo Fallback = CultureInfo.GetCultureInfo("en-GB");

    // Only English for now: en-US keeps its own format (12 hour clocks), everything else gets en-GB. Runs first, before
    // any thread is started
    public static void Apply()
    {
        var format = IsUs(CultureInfo.CurrentCulture) ? CultureInfo.CurrentCulture : Fallback;
        var ui = IsUs(CultureInfo.CurrentUICulture) ? CultureInfo.CurrentUICulture : Fallback;
        CultureInfo.DefaultThreadCurrentCulture = format;
        CultureInfo.DefaultThreadCurrentUICulture = ui;
        CultureInfo.CurrentCulture = format;
        CultureInfo.CurrentUICulture = ui;
    }

    private static bool IsUs(CultureInfo c) => c.Name == "en-US";
}
