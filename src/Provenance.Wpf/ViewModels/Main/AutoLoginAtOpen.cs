using Provenance.Common.Credentials;
using Provenance.Common.Login;
using Provenance.Common.Settings;
using Serilog;

namespace Provenance.Wpf.ViewModels.Main;

// the app opening and logging in by itself, worked out from the settings before the main window exists. The same
// conditions as Startup's, which still has the last word
internal static class AutoLoginAtOpen
{
    // off and saved, and it stays off until the player turns it back on
    internal static void TurnOffForShift(ISettingsService settings) => settings.SaveChange(s => s.AutoLogin = false);

    internal static Account? Coming(ISettingsService settings, ICredentialService credentials, bool suppressed)
    {
        try
        {
            var s = settings.Current;
            var profile = AccountsViewModel.ProfileAtStart(s);
            if (profile is null || !MainViewModel.LoginAtStart(s.AutoLogin, suppressed, AccountsViewModel.SavedDetails(credentials, profile))) return null;
            // a new install's Windower/Ashita question comes before its first login
            if (MainViewModel.SetupCardWaits(s.SetupPending, MainViewModel.GameInstalledIn(settings.DetectedRegion), true)) return null;
            return profile;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not tell whether an automatic login's coming");
            return null;
        }
    }

    internal static void StartAhead(PolSession pol, Account? account)
    {
        if (account is null) return;
        // another copy's logged in with it, so this login's going to be turned down
        if (PolSession.HeldByAnotherCopy(account.Id)) return;
        try { pol.StartAhead(account.Id, new SessionOptions(false, null)); }
        catch (Exception ex) { Log.Warning(ex, "The login's early start failed, it starts with the login instead"); }
    }
}
