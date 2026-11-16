namespace Provenance.Wpf;

public sealed class LaunchArgs
{
    public const string NoAutoLoginSwitch = "--noautologin";

    // what a restart after an update passes when Shift was held, since the held key is gone by then. That start turns
    // Log In Automatically off, where --noautologin only skips the one start
    public const string ShiftHeldSwitch = "--shift-held";

    // the restart after an update passes it, so that start (and only that one) can tell an update that never went in
    public const string AfterUpdateSwitch = "--after-update";

    public string[] All { get; }
    public bool NoAutoLogin { get; }
    public bool AfterUpdate { get; }
    public bool ShiftHeld { get; }

    public LaunchArgs(string[] args)
    {
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        All = args;
        NoAutoLogin = Has(NoAutoLoginSwitch);
        AfterUpdate = Has(AfterUpdateSwitch);
        ShiftHeld = Has(ShiftHeldSwitch);
    }

    // the args for the copy Update.exe starts: a Shift held now would be gone by then
    public string[] ForRestart(bool shiftHeld) =>
        shiftHeld && !ShiftHeld
            ? [.. All, ShiftHeldSwitch, AfterUpdateSwitch]
            : [.. All, AfterUpdateSwitch];
}
