namespace Provenance.Common.Game.Windower;

public sealed record ConsoleKeyChoice(WindowerKey Key, string Display)
{
    public override string ToString() => Display;
}
