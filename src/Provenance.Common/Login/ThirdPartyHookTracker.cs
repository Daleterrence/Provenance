using Provenance.Common.Game;
using Serilog;

namespace Provenance.Common.Login;

internal sealed class ThirdPartyHookTracker
{
    private readonly object _gate = new();
    private ProcessStamp? _hook;

    internal void Track(ProcessStamp hook)
    {
        lock (_gate) _hook = hook;
    }

    internal ProcessStamp? Take()
    {
        lock (_gate)
        {
            var taken = _hook;
            _hook = null;
            return taken;
        }
    }

    internal void Forget()
    {
        lock (_gate) _hook = null;
    }

    internal void EndNow() => End(Take());

    // Ashita-cli closes by itself once the game's up, so it only gets a couple of secs before it's ended
    internal static void End(ProcessStamp? hook)
    {
        if (hook is not { } stamp) return;
        var name = Path.GetFileNameWithoutExtension(ProcessTree.ImagePath(stamp.Pid)) ?? "Windower or Ashita";
        if (stamp.EndIfStill(graceMs: 2000) == ProcessStamp.Ending.Ended)
            Log.Information("Ended {Name} (pid {Pid}): the game was not started", name, stamp.Pid);
    }
}
