namespace Provenance.Wpf.ViewModels;

public sealed record HookProfileItem(string Name, string Display)
{
    public override string ToString() => Display;
}
