using Serilog.Events;
using Velopack.Logging;

namespace Provenance.Wpf.Services;

internal sealed class VelopackLog : IVelopackLogger
{
    public void Log(VelopackLogLevel level, string? message, Exception? exception)
    {
        var to = level switch
        {
            VelopackLogLevel.Trace => LogEventLevel.Verbose,
            VelopackLogLevel.Debug => LogEventLevel.Debug,
            VelopackLogLevel.Information => LogEventLevel.Information,
            VelopackLogLevel.Warning => LogEventLevel.Warning,
            VelopackLogLevel.Error => LogEventLevel.Error,
            _ => LogEventLevel.Fatal,
        };
        Serilog.Log.Write(to, exception, "Velopack: {Message}", message);
    }
}
