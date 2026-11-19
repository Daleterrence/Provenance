using Provenance.Common.Game;
using Provenance.Common.Login;

namespace Provenance.Wpf.ViewModels.Main;

public enum SessionPhase { LoggedOut, LoggingIn, LoggedIn, UpdatingGame, StartingGame, InGame, LoggingOut, MultiRun, MultiStopping }

public sealed class SessionState(PolSession? pol = null)
{
    private static readonly HashSet<(SessionPhase From, SessionPhase To)> Moves =
    [
        (SessionPhase.LoggedOut, SessionPhase.LoggingIn),
        (SessionPhase.LoggingOut, SessionPhase.LoggingIn),
        (SessionPhase.LoggingIn, SessionPhase.LoggedIn),
        (SessionPhase.LoggingIn, SessionPhase.LoggedOut),
        (SessionPhase.LoggingIn, SessionPhase.StartingGame),
        (SessionPhase.LoggedIn, SessionPhase.UpdatingGame),
        (SessionPhase.UpdatingGame, SessionPhase.StartingGame),
        (SessionPhase.UpdatingGame, SessionPhase.LoggedIn),
        (SessionPhase.StartingGame, SessionPhase.LoggedIn),
        (SessionPhase.StartingGame, SessionPhase.InGame),
        (SessionPhase.InGame, SessionPhase.LoggedOut),
        (SessionPhase.LoggedIn, SessionPhase.LoggingOut),
        (SessionPhase.LoggingOut, SessionPhase.LoggedOut),
        (SessionPhase.LoggedOut, SessionPhase.MultiRun),
        (SessionPhase.LoggingOut, SessionPhase.MultiRun),
        (SessionPhase.MultiRun, SessionPhase.MultiStopping),
        (SessionPhase.MultiRun, SessionPhase.LoggedOut),
        (SessionPhase.MultiStopping, SessionPhase.LoggedOut),
    ];

    private volatile SessionPhase _phase;

    public SessionPhase Phase => _phase;

    public event Action? PhaseChanged;

    public event Action? RunChanged;

    public int? Pid => pol?.Pid;

    public bool LoggedIn => _phase is SessionPhase.LoggedIn or SessionPhase.UpdatingGame or SessionPhase.StartingGame or SessionPhase.InGame;

    public bool Launching => _phase is SessionPhase.LoggingIn or SessionPhase.UpdatingGame or SessionPhase.StartingGame or SessionPhase.InGame;

    public bool GameRunning => _phase is SessionPhase.UpdatingGame or SessionPhase.StartingGame or SessionPhase.InGame;

    public bool InMultiRun => _phase is SessionPhase.MultiRun or SessionPhase.MultiStopping;

    public bool IdleForReuse => !LoggedIn && !Launching && !InMultiRun;

    public bool PolInUse => Launching || LoggedIn || Pid is not null;

    public string? LargeAddressAwareBlocked() =>
        PolInUse || _phase == SessionPhase.LoggingOut ? LargeAddressAwareSwitch.LoggedInText
        : InMultiRun ? LargeAddressAwareSwitch.MultiRunText
        : null;

    internal void Set(SessionPhase next)
    {
        _phase = next;
        PhaseChanged?.Invoke();
    }

    internal void NoteRunChanged() => RunChanged?.Invoke();

    internal static bool IsAllowed(SessionPhase from, SessionPhase to) => Moves.Contains((from, to));
}

internal struct LastRaised
{
    private bool _last;

    public bool Changed(bool now)
    {
        if (now == _last) return false;
        _last = now;
        return true;
    }
}
