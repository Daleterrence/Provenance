namespace Provenance.Wpf;

// every command-line switch the app reads
public sealed class LaunchArgs
{
    public const string NoAutoLoginSwitch = "--noautologin";

    // the restart after an update passes both. The first stops a failed apply looping, the second means only that
    // start (not a shortcut that has the first) judges how the update went
    public const string SkipUpdateCheckSwitch = "--no-update-check";
    public const string AfterUpdateSwitch = "--after-update";

    public string[] All { get; }
    public bool NoAutoLogin { get; }
    public bool SkipUpdateCheck { get; }
    public bool AfterUpdate { get; }

    public LaunchArgs(string[] args)
    {
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        All = args;
        NoAutoLogin = Has(NoAutoLoginSwitch);
        SkipUpdateCheck = Has(SkipUpdateCheckSwitch);
        AfterUpdate = Has(AfterUpdateSwitch);
    }

    // the args for the copy Update.exe starts: a Shift held now would be gone by then
    public string[] ForRestart(bool shiftHeld) =>
        shiftHeld && !NoAutoLogin
            ? [.. All, NoAutoLoginSwitch, SkipUpdateCheckSwitch, AfterUpdateSwitch]
            : [.. All, SkipUpdateCheckSwitch, AfterUpdateSwitch];
}
