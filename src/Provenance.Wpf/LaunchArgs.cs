namespace Provenance.Wpf;

public sealed class LaunchArgs
{
    public const string NoAutoLoginSwitch = "--noautologin";

    public const string ShiftHeldSwitch = "--shift-held";

    public const string AfterUpdateSwitch = "--after-update";

    public const string UpdateHeldSwitch = "--update-held";

    public string[] All { get; }
    public bool NoAutoLogin { get; }
    public bool AfterUpdate { get; }
    public bool ShiftHeld { get; }
    public bool UpdateHeld { get; }

    public LaunchArgs(string[] args)
    {
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        All = args;
        NoAutoLogin = Has(NoAutoLoginSwitch);
        AfterUpdate = Has(AfterUpdateSwitch);
        ShiftHeld = Has(ShiftHeldSwitch);
        UpdateHeld = Has(UpdateHeldSwitch);
    }

    public string[] ForRestart(bool shiftHeld)
    {
        var kept = All.Where(a => !a.Equals(UpdateHeldSwitch, StringComparison.OrdinalIgnoreCase));
        return shiftHeld && !ShiftHeld
            ? [.. kept, ShiftHeldSwitch, AfterUpdateSwitch]
            : [.. kept, AfterUpdateSwitch];
    }

    // Restart Now closes a game that's logged in, and the server can take a moment to notice, so the restart doesn't log in by itself
    public static string[] ForRestartNow() => [NoAutoLoginSwitch, AfterUpdateSwitch];
}
