using Provenance.Wpf.Services;
using Velopack;

namespace Provenance.Wpf;

public static class Program
{
    internal static Exception? VelopackStartError { get; private set; }

    [STAThread]
    public static void Main()
    {
        ThreadStaticsFix.Apply();
        // before any window: it gives the windows the shortcuts' taskbar id, and UpdateManager can't find the install
        // without it. Updates only get applied from the update window, never here
        if (SelfUpdate.InstalledCopy)
        {
            try { VelopackApp.Build().SetAutoApplyOnStartup(false).SetLogger(new VelopackLog()).Run(); }
            catch (Exception ex) { VelopackStartError = ex; }
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
