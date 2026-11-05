namespace Provenance.Common.Game.Windower;

public static class WindowerUpdates
{
    public static WindowerUpdater NewUpdater(string dir, string branch) =>
        new(dir, branch) { Log = line => Serilog.Log.Debug("Windower update: {Line}", line) };
}
