using System.Diagnostics;
using Serilog;

namespace Provenance.Common.Logging;

public sealed class StepTimer(string what)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _last;

    public void Lap(string step)
    {
        long now = _clock.ElapsedMilliseconds;
        Log.Debug("{What}: {Step} took {Ms}ms", what, step, now - _last);
        _last = now;
    }
}
