namespace Provenance.Wpf.ViewModels.Shared;

public sealed record Choice<T>(T Code, string Text)
{
    public override string ToString() => Text;
}
