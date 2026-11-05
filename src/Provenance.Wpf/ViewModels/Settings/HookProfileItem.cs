namespace Provenance.Wpf.ViewModels.Settings;

public sealed record HookProfileItem(string Name, string Display)
{
    public override string ToString() => Display;
}
