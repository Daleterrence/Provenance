using Provenance.Common.Patcher;

namespace Provenance.Common.Login;

public enum LaunchProgressKind { Progress, Notice, Failed, Stopped }

public sealed record LaunchProgress(string Stage, string Detail, double Fraction = -1, PatchProgress? Patch = null,
                                    LaunchProgressKind Kind = LaunchProgressKind.Progress)
{
    public const double LogInUpdateEnd = 0.5;

    public static LaunchProgress Updating(string stage, PatchProgress p, double from, double to) =>
        new(stage, p.Text, Share(p, from, to), p);

    public static LaunchProgress Failed(string text) => new("Failed", text, Kind: LaunchProgressKind.Failed);

    public static LaunchProgress Notice(string text) => new("Notice", text, Kind: LaunchProgressKind.Notice);

    public static LaunchProgress Cancelled(bool polClosed = false) =>
        new("Stopped", polClosed ? "The login was cancelled, and PlayOnline has been closed." : "The login was cancelled.",
            Kind: LaunchProgressKind.Stopped);

    private static double Share(PatchProgress p, double from, double to) => p.Fraction < 0 ? -1 : p.Phase switch
    {
        PatchPhase.Scan => from + (to - from) * 0.2 * p.Fraction,
        PatchPhase.Download => from + (to - from) * (0.2 + 0.8 * p.Fraction),
        _ => -1,
    };
}
