using Serilog;
using Serilog.Events;

namespace Provenance.Common.Logging;

public static class LogSetup
{
    public const string AppTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    // <name>-<date>.log in the folder, one file a day. No template means Serilog's own
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
