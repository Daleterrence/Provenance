namespace Provenance.Common.Game.Windower;

internal sealed record WindowerUpdateAnswer(WindowerUpdateResult? Update, string? Notice);

internal sealed class WindowerUpdateAnswers
{
    public WindowerUpdateAnswer? Value { get; set; }
}
