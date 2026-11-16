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

    [STAThread]
    public static void Main()
    {
        AppCulture.Apply();
        ThreadStaticsFix.Apply();
        // before anything's written to ProgramData, so Provenance's own files never look planted to the folder check
        bool ownsNewFiles = ProtectedFolders.OwnNewFilesAsAdministrators();
        // never the player's own folders, a link there would be written through. No safe folder means no file log
        if (LogSetup.ElevatedFolder() is { } logs) StartLogging(logs);
        if (!ownsNewFiles) Log.Warning("New files won't be Administrators' if this PC's policy says otherwise");
        // before any window: it gives the windows the shortcuts' taskbar id, and UpdateManager can't find the install
        // without it. Updates only get applied from the update window, never here
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
