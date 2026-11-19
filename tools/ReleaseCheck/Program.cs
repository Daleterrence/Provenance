using Provenance.Common.Mirror;
using Provenance.ReleaseCheck;
using Velopack.Sources;

switch (args)
{
    case ["--live"]:
        return await ReleaseGate.CheckLiveAsync(ReleaseGate.UpdateServer, MirrorKeys.Trusted, new HttpClientFileDownloader(), Console.Out) ? 0 : 1;
    case ["--live", var server]:
        return await ReleaseGate.CheckLiveAsync(server, MirrorKeys.Trusted, new HttpClientFileDownloader(), Console.Out) ? 0 : 1;
    case [var folder, var version] when !folder.StartsWith("--", StringComparison.Ordinal):
        return ReleaseGate.CheckFolder(folder, version, MirrorKeys.Trusted, Console.Out) ? 0 : 1;
    default:
        Console.Error.WriteLine("usage: ReleaseCheck <folder> <version>   a release's files, as the GitHub release has them");
        Console.Error.WriteLine("       ReleaseCheck --live [<server>]    what the update server serves, each channel's newest release");
        return 2;
}
