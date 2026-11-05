using Provenance.Common.Patcher;

namespace Provenance.Wpf.Services;

// Patch is an update's own progress, mapped into its share of the bar
public sealed record LaunchProgress(string Stage, string Detail, int Step = -1, double Fraction = -1, PatchProgress? Patch = null)
{
    // a viewer update at sign-in gets the bar up to here, and the sign-in's own steps the rest
    public const double SignInUpdateEnd = 0.5;

    public static LaunchProgress Updating(string stage, int step, PatchProgress p, double from, double to) =>
        new(stage, p.Text, step, Share(p, from, to), p);

    // checking the files is about a fifth of the update's share
    private static double Share(PatchProgress p, double from, double to) => p.Fraction < 0 ? -1 : p.Phase switch
    {
        PatchPhase.Scan => from + (to - from) * 0.2 * p.Fraction,
        PatchPhase.Download => from + (to - from) * (0.2 + 0.8 * p.Fraction),
        _ => -1,
    };
}
