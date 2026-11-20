using Provenance.Common.Security;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace Provenance.Common.Logging;

public static class LogSetup
{
    public const string AppTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    private const string PlainTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    public static string? ElevatedFolder()
    {
        try { return ProtectedFolders.RootSubDir(AppPaths.LogsFolder, usersMayRead: true); }
        catch (Exception) { return null; }
    }

    public static void ToDailyFile(string folder, string name, int keepFiles, LogEventLevel minimum = LogEventLevel.Information,
                                   string? outputTemplate = null)
    {
        Directory.CreateDirectory(folder);
        var lines = new WithoutUserNames(new MessageTemplateTextFormatter(outputTemplate ?? PlainTemplate));
        Log.Logger = new LoggerConfiguration().MinimumLevel.Is(minimum)
            .WriteTo.File(lines, Path.Combine(folder, name + "-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: keepFiles)
            .CreateLogger();
    }

    private sealed class WithoutUserNames(ITextFormatter lines) : ITextFormatter
    {
        public void Format(LogEvent logEvent, TextWriter output)
        {
            using var line = new StringWriter();
            lines.Format(logEvent, line);
            output.Write(UserNames.Hide(line.ToString()));
        }
    }
}
