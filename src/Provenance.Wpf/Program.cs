using Provenance.Common;
using Provenance.Wpf.Services;
using Velopack;

namespace Provenance.Wpf;

public static class Program
{
    internal static Exception? VelopackStartError { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        ThreadStaticsFix.Apply();
        // before any window: it gives the windows the shortcuts' taskbar id, and UpdateManager can't find the install
        // without it. Updates only get applied from the update window, never here
        if (SelfUpdate.InstalledCopy)
        {
            try { VelopackApp.Build().SetAutoApplyOnStartup(false).SetLogger(new VelopackLog()).Run(); }
            catch (Exception ex) { VelopackStartError = ex; }
        }
        // the hooks go to the launcher, but if one ever lands here it mustn't start the app
        if (args.Length > 0 && (args[0].StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase) ||
                                args[0].StartsWith("--squirrel-", StringComparison.OrdinalIgnoreCase)))
            return;

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
