using System.IO;
using System.Windows;
using System.Windows.Input;
using Provenance.Common;
using Provenance.Common.Credentials;
using Provenance.Common.Feeds;
using Provenance.Common.Game;
using Provenance.Common.Hook;
using Provenance.Common.Login;
using Provenance.Common.Otp;
using Provenance.Common.Security;
using Provenance.Common.Settings;
using Provenance.Wpf.Win32;
using Serilog;
using Provenance.Wpf.Services;
using Provenance.Wpf.Views.Main;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf;

public partial class App : Application
{
    private PolSession? _pol;
    private bool _showingError;   // an error inside the card mustn't stack more cards

    internal static bool RestartOnExit { get; set; }

    internal static bool SkipStartup { get; set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (SkipStartup) return;
        base.OnStartup(e);
        var args = new LaunchArgs(e.Args);
        // first thing: the update check (or a whole update) can outlast a held Shift
        bool shiftAtStart = ShiftDown();
        Log.Information("Provenance starting");
        // before anything's written to ProgramData, so Provenance's own files never look planted to the folder check
        if (!ProtectedFolders.OwnNewFilesAsAdministrators()) Log.Warning("New files won't be Administrators' if this PC's policy says otherwise");
        // before the first window, the update one included, so nothing opens in the other theme first
        AppTheme.Apply(AppTheme.FromSettingsFile(SettingsService.DefaultPath));

        if (!args.AfterUpdate && TrayCopy.ReuseRunning())
        {
            Shutdown(0);
            return;
        }

        // first, so an update can restart the app straight away. Closing the update window or the error card mustn't end the app
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (await SelfUpdate.RunAsync(args, noAutoLogin: shiftAtStart))
        {
            Shutdown(0);
            return;
        }

        // admin-only before any service starts, or a user-level program could plant hook files or hold a lock
        if (!PrepareProtectedFolders())
        {
            Shutdown(1);
            return;
        }
        SweepOldHookFiles();

        // taken here on the UI thread, since the settings are built lazily on whichever thread asks first
        var uiThread = SynchronizationContext.Current;
        if (uiThread is null) Log.Warning("The settings were built with no UI thread to hand changes to");
        var credentials = new CredentialService();
        var settings = new SettingsService(SettingsService.DefaultPath, uiThread);
        var pol = _pol = new PolSession(settings);

        WarmKeys();
        CatchUnhandled();

        var mainVm = ShowMainWindow(credentials, settings, pol);
        SelfUpdate.TellIfUpdateFailed(mainVm.Notice);
        StartMainViewModel(mainVm, shiftAtStart || ShiftDown() || args.NoAutoLogin);
    }

    private static bool PrepareProtectedFolders()
    {
        try
        {
            HookFiles.Dir = ProtectedFolders.SubDir("hook");
            AdminLock.Dir = ProtectedFolders.SubDir("locks");
            return true;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Could not prepare the protected folder for the hook's files");
            StartupErrors.Show("Provenance can't start", "Provenance couldn't set up its folder in ProgramData. Close any program that's using that " +
                               "folder, then start Provenance again.", offerLogs: true);
            return false;
        }
    }

    // a snapshot holds friends' messages, so it goes once its pol.exe has
    private static void SweepOldHookFiles()
    {
        int swept = HookFiles.Sweep(HookFiles.RequiredDir, ProcessStamp.PidRunning, TimeSpan.FromDays(7), DateTime.UtcNow);
        if (swept > 0) Log.Information("Removed {Count} old hook file(s)", swept);
    }

    private static void WarmKeys()
    {
        _ = CredentialKey.WarmUp();
        _ = OtpReuse.OpenKeyInBackground();
    }

    private void CatchUnhandled()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception");
            args.Handled = true;
            if (_showingError) return;
            _showingError = true;
            try
            {
                if (KeyUnavailable(args.Exception) is { } keyProblem)
                    StartupErrors.Show("Can't open your saved login details", keyProblem.Message, offerLogs: true, UnexpectedText);
                else
                    StartupErrors.Show("Provenance ran into a problem",
                        "It's still running, but if something isn't working, close Provenance and open it again. Press Open logs to see what went wrong.",
                        offerLogs: true, UnexpectedText);
            }
            finally { _showingError = false; }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error(args.ExceptionObject as Exception, "Unhandled AppDomain exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
        // without "Provenance exiting" before it, something ended the process outside WPF's shutdown
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.Information("Process exit");
    }

    private MainViewModel ShowMainWindow(CredentialService credentials, SettingsService settings, PolSession pol)
    {
        // the friends windows are unowned, so the app ends with the main window (the tray only hides it)
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var mainWindow = new MainWindow();
        var windows = new WindowManager(settings, WpfDialogs.Default);
        var mainVm = new MainViewModel(WpfDialogs.Default, windows, credentials, settings, new FeedService(settings), pol, new AvatarService(settings));
        // after the view model's listening for a failed save, before its start-up clears the records of gone pol.exes or
        // pre-starts one
        ScreenSaver.Note(settings);
        // Windows logging off cancels a log in, and WPF would close the window ignoring its Cancel, so it closes normally first.
        // A held session is logged out, nothing goes to tray
        SessionEnding += (_, e) =>
        {
            if (LogOff(mainVm, mainWindow)) e.Cancel = true;
        };
        mainWindow.DataContext = mainVm;
        MainWindow = mainWindow;
        mainWindow.Show();
        if (settings.Current.MinimizeToTray) TrayIcon.Warm();
        // this one really is off the UI thread, the wait's on the pool
        if (SelfUpdate.InstalledCopy) CloseRequest.Listen(() => Dispatcher.InvokeAsync(mainWindow.ExitWithoutAsking));
        return mainVm;
    }

    internal static bool LogOff(MainViewModel mainVm, MainWindow mainWindow)
    {
        Log.Information("Windows session ending");
        _ = mainVm.CancelLogInAsync();
        bool refuse = mainWindow.ExitWaitsForAnswer;
        mainWindow.ExitWithoutAsking();
        return refuse;
    }

    private static void StartMainViewModel(MainViewModel mainVm, bool suppressed)
    {
        _ = Task.Run(() => Provenance.Common.Feeds.BannerImages.Prune(TimeSpan.FromDays(60)));
        _ = mainVm.News.LoadFeedsAsync();
        _ = mainVm.News.LoadWorldsAsync();
        mainVm.Startup(suppressed);
    }

    private const string UnexpectedText =
        "Provenance ran into a problem it didn't expect, but it's still running. If something isn't working, close " +
        "Provenance and open it again.";

    // the async state as well, a thread that hasn't read any input yet can miss a key already held
    private static bool ShiftDown() =>
        Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) || (User32.GetAsyncKeyState(VkShift) & 0x8000) != 0;

    private const int VkShift = 0x10;

    internal static CredentialKeyUnavailableException? KeyUnavailable(Exception ex) => ex switch
    {
        CredentialKeyUnavailableException key => key,
        AggregateException { InnerExceptions: [CredentialKeyUnavailableException key] } => key,
        _ => null,
    };

    // no await, WPF kills the dispatcher when OnExit returns and a held session would never get logged out. Every wait
    // is time-limited on the pool
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("Provenance exiting (code {Code})", e.ApplicationExitCode);
            try { Task.Run(Bitwarden.LockAtExitAsync).Wait(TimeSpan.FromSeconds(5)); } catch { }   // exit goes ahead without it
            if (_pol is { } pol) EndSession(pol);
            if (RestartOnExit) StartAgain();
            else if (!SelfUpdate.HandOverAfterExit(out var restartArgs)) StartAgain(restartArgs);
        }
        finally
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

    // after the session's ended, so a held POL login is already logged out by the time the new copy starts
    private static void StartAgain(params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var started = System.Diagnostics.Process.Start(psi);
            Log.Information("Started Provenance again (pid {Pid})", started?.Id);
        }
        catch (Exception ex) { Log.Error(ex, "Could not start Provenance again"); }
    }

    // every wait in Dispose has its own limit, cut it short and POL stays logged in
    internal static void EndSession(IDisposable session)
    {
        try { session.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "Ending the PlayOnline session failed"); }
    }
}
