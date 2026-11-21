using Provenance.Common;
using Provenance.Common.Logging;
using Provenance.Common.Security;
using Provenance.Wpf.Services;
using Serilog;
using Serilog.Events;
using Velopack;

namespace Provenance.Wpf;

public static class Program
{
    internal static void StartLogging(string folder) =>
        LogSetup.ToDailyFile(folder, "launcher", 14, LogEventLevel.Debug, LogSetup.AppTemplate);

    internal static string? LogsProblem { get; private set; }

    [STAThread]
    public static void Main()
    {
        AppCulture.Apply();
        ThreadStaticsFix.Apply();
        bool ownsNewFiles = ProtectedFolders.OwnNewFilesAsAdministrators();
        // never the player's own folders, a link there would be written through
        if (LogSetup.ElevatedFolder(out var problem) is { } logs) StartLogging(logs);
        else LogsProblem = problem;
        if (!ownsNewFiles) Log.Warning("New files won't be Administrators' if this PC's policy says otherwise");
        // before any window, UpdateManager can't find the install without it
        if (SelfUpdate.InstalledCopy)
        {
            try { VelopackApp.Build().SetAutoApplyOnStartup(false).SetLogger(new VelopackLog()).Run(); }
            catch (Exception ex) { Log.Warning(ex, "Velopack start-up failed"); }
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
