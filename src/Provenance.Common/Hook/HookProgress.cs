using Provenance.Common.Login;

namespace Provenance.Common.Hook;

public sealed class HookProgress
{
    private readonly HashSet<string> _seen = new();
    private bool _ready;
    private int _loadsDone;

    // polcore's login state machine (the fractions only keep the bar moving, don't read much into them)
    public static (string Text, double Fraction) AuthStateText(int state) => state switch
    {
        18 or 19 or 20 or 21 or 22 => ("Connecting to PlayOnline...", 0.10),
        23 => ("Checking your PlayOnline ID and password...", 0.18),
        24 => ("Checking your Square Enix account...", 0.40),
        25 or 26 or 27 or 28 => ("Checking your Square Enix account...", 0.45),
        40 or 41 => ("Opening your PlayOnline session...", 0.56),
        29 or 30 => ("Opening your PlayOnline session...", 0.64),
        _ => ("Logging in to PlayOnline...", -1),
    };

    private LaunchProgress? Once(string key, LaunchProgress report) => _seen.Add(key) ? report : null;

    private LaunchProgress? Once(string key, string stage, string detail, double fraction = -1) =>
        Once(key, new LaunchProgress(stage, detail, fraction));

    public LaunchProgress? Step(HookEvent? e)
    {
        switch (e)
        {
            case ViewerStarted:
                return Once("vi", "Starting", "Starting PlayOnline...", 0.06);
            case RealAuthStarted:
                return Once("auth", "Signing in", "Connecting to PlayOnline...", 0.08);
            case AuthStateChanged s when !_ready:
                var (text, f) = AuthStateText(s.State);
                return Once("st:" + text, "Signing in", text, f);
            case AuthSucceeded:
                return Once("ok", "Signed in", "Loading your friends and profile...", 0.68);
            case ListLoaded when !_ready:
                _loadsDone++;
                return new LaunchProgress("Signed in", "Loading your friends and profile...",
                                          Math.Min(0.68 + 0.04 * _loadsDone, 0.88));
            case ContentIdsCounted { Ffxi: 0 }:
                return Once("cids", "Signed in", "This account has no Content ID for FINAL FANTASY XI.", 0.9);
            case SessionHeld:
                _ready = true;
                return Once("held", "Connected", "Going online...", 0.95);
            case StopRequested:
                return Once("stop", LaunchProgress.Cancelled(polClosed: true));
            default:
                return null;
        }
    }
}
