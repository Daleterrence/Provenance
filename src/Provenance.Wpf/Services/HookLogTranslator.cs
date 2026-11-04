using System.Text.RegularExpressions;

namespace Provenance.Wpf.Services;

public sealed class HookLogTranslator
{
    private static readonly Regex AuthState = new(@"auth SM: state=(\d+)", RegexOptions.Compiled);
    private static readonly Regex ProfileDone = new(@"profile: (\w+) -> (-?\d+)", RegexOptions.Compiled);
    private static readonly Regex ContentIds = new(@"content ids: (\d+) valid, (\d+) FFXI", RegexOptions.Compiled);

    private readonly HashSet<string> _seen = new();
    private bool _ready;
    private int _loadsDone;

    // polcore's sign-in state machine (the fractions only keep the bar moving, don't read much into them)
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

    private LaunchProgress? Once(string key, string stage, string detail, int step = -1, double fraction = -1) =>
        _seen.Add(key) ? new LaunchProgress(stage, detail, step, fraction) : null;

    public LaunchProgress? Translate(string line)
    {
        if (line.Contains("viewer_init: lang="))
            return Once("vi", "Starting", "Starting PlayOnline...", LaunchSteps.StartPol, 0.06);
        if (line.Contains("phase_c: REAL auth"))
            return Once("auth", "Signing in", "Connecting to PlayOnline...", LaunchSteps.SignIn, 0.08);
        if (AuthState.Match(line) is { Success: true } s && !_ready)
        {
            var (text, f) = AuthStateText(int.Parse(s.Groups[1].Value));
            return Once("st:" + text, "Signing in", text, LaunchSteps.SignIn, f);
        }
        if (line.Contains("phase_c: AUTH OK"))
            return Once("ok", "Signed in", "Loading your friends and profile...", LaunchSteps.Profile, 0.68);
        if (!_ready && ProfileDone.IsMatch(line))
        {
            _loadsDone++;
            return new LaunchProgress("Signed in", "Loading your friends and profile...", LaunchSteps.Profile,
                                      Math.Min(0.68 + 0.04 * _loadsDone, 0.88));
        }
        if (ContentIds.Match(line) is { Success: true } ci && ci.Groups[2].Value == "0")
            return Once("cids", "Signed in", "This account has no Content ID for FINAL FANTASY XI.", LaunchSteps.Profile, 0.9);
        if (line.Contains("hold: POL-only session held"))
        {
            _ready = true;
            return Once("held", "Connected", "Going online...", LaunchSteps.StartGame, 0.95);
        }
        if (line.Contains("phase_c: stop requested"))
            return Once("stop", "Stopped", "The login was cancelled, and PlayOnline has been closed.");
        return null;
    }
}
