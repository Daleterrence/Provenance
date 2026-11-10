using Provenance.Common.Game;
using Provenance.Common.Install;
using Serilog;

namespace Provenance.Common.Login;

internal sealed class OurPol
{
    internal Func<ProcessStamp, bool> IsViewerPol { get; init; } = stamp => stamp.IsViewerPol();

    internal bool Ours(ProcessStamp stamp) => stamp.IsRunning() && IsViewerPol(stamp);

    internal bool End(ProcessStamp stamp, int graceMs = 0, int afterMs = 0) => End(stamp, graceMs, afterMs, IsViewerPol);

    internal static void EndNewPol(int pid, ProcessStamp launcher)
    {
        bool NewPol(ProcessStamp pol) => pol.StartUtcTicks >= launcher.StartUtcTicks
            && string.Equals(Path.GetFileName(ProcessTree.ImagePath(pol.Pid)), GameProduct.Viewer.KeyFile, StringComparison.OrdinalIgnoreCase);
        if (ProcessStamp.Of(pid) is { } found && End(found, 0, 2000, NewPol))
            Log.Information("Ended pol.exe (pid {Pid}): the sign-in was cancelled", pid);
    }

    private static bool End(ProcessStamp stamp, int graceMs, int afterMs, Func<ProcessStamp, bool> alsoIf)
    {
        var result = stamp.EndIfStill(graceMs, afterMs, alsoIf);
        if (result != ProcessStamp.Ending.NotThat) ScreenSaver.PutBack();
        return result == ProcessStamp.Ending.Ended;
    }
}
