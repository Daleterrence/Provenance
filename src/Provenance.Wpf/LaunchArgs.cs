namespace Provenance.Wpf;

public sealed class LaunchArgs
{
    public const string NoAutoLoginSwitch = "--noautologin";

    // the restart after an update passes it, so that start (and only that one) can tell an update that never went in
    public const string AfterUpdateSwitch = "--after-update";

    public string[] All { get; }
    public bool NoAutoLogin { get; }
    public bool AfterUpdate { get; }

    public LaunchArgs(string[] args)
    {
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        All = args;
        NoAutoLogin = Has(NoAutoLoginSwitch);
        AfterUpdate = Has(AfterUpdateSwitch);
    }

    // the args for the copy Update.exe starts: a Shift held now would be gone by then
    public string[] ForRestart(bool shiftHeld) =>
        shiftHeld && !NoAutoLogin
            ? [.. All, NoAutoLoginSwitch, AfterUpdateSwitch]
            : [.. All, AfterUpdateSwitch];
}
