using Provenance.Common.Security;
using Serilog;
using Serilog.Events;

namespace Provenance.Common.Logging;

public static class LogSetup
{
    public const string AppTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    // the elevated app's logs, where only Administrators and SYSTEM can write and the player can still read them. Null
    // when that can't be made
    public static string? ElevatedFolder()
    {
        try { return ProtectedFolders.RootSubDir(AppPaths.LogsFolder, usersMayRead: true); }
        catch (Exception) { return null; }
    }

    public static void ToDailyFile(string folder, string name, int keepFiles, LogEventLevel minimum = LogEventLevel.Information,
                                   string? outputTemplate = null)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + "-.log");
        var config = new LoggerConfiguration().MinimumLevel.Is(minimum);
        Log.Logger = (outputTemplate is null
            ? config.WriteTo.File(path, rollingInterval: RollingInterval.Day, retainedFileCountLimit: keepFiles)
            : config.WriteTo.File(path, rollingInterval: RollingInterval.Day, retainedFileCountLimit: keepFiles, outputTemplate: outputTemplate))
            .CreateLogger();
    }
}
