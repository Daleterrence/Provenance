namespace Provenance.Common.Hook;

public abstract record HookEvent(string Line);

public sealed record ViewerStarted(string Line, int? Language) : HookEvent(Line);
public sealed record RealAuthStarted(string Line) : HookEvent(Line);
public sealed record AuthStateChanged(string Line, int State) : HookEvent(Line);
public sealed record AuthSucceeded(string Line) : HookEvent(Line);
public sealed record ListLoaded(string Line) : HookEvent(Line);
public sealed record ContentIdsCounted(string Line, int Valid, int Ffxi) : HookEvent(Line);
public sealed record SessionHeld(string Line) : HookEvent(Line);
public sealed record StopRequested(string Line) : HookEvent(Line);

public sealed record DetailsUnreadable(string Line) : HookEvent(Line);
public sealed record AuthRefused(string Line, int? Reason) : HookEvent(Line);
public sealed record AccountBlocked(string Line, string Notice) : HookEvent(Line);
public sealed record ListLoadFailed(string Line, string List) : HookEvent(Line);
public sealed record NoHandle(string Line) : HookEvent(Line);
public sealed record HandleRefused(string Line) : HookEvent(Line);
public sealed record NoSession(string Line) : HookEvent(Line);
public sealed record HandleWanted(string Line) : HookEvent(Line);
public sealed record Crashed(string Line) : HookEvent(Line);

public sealed record PlayStarted(string Line) : HookEvent(Line);
public sealed record PlayUnreadable(string Line) : HookEvent(Line);
public sealed record PlayRefused(string Line) : HookEvent(Line);

public sealed record PrestartReady(string Line) : HookEvent(Line);
