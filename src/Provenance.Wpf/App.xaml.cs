using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Provenance.Common.Hook;
using Provenance.Common.Security;
using Serilog;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels;
using Provenance.Wpf.Views;

namespace Provenance.Wpf;

public partial class App : Application
{
    private IHost? _host;
    private bool _showingError;   // an error inside the card mustn't stack more cards

    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not started");

    internal static bool RestartOnExit { get; set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // first thing: the update check (or a whole update) can outlast a held Shift
        bool shiftAtStart = ShiftDown();
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                  "Provenance", "logs");
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(logDir, "launcher-.log"), rollingInterval: RollingInterval.Day,
                          retainedFileCountLimit: 14,
                          outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("Provenance starting");
        // before anything's written to ProgramData, so Provenance's own files never look planted to the folder check
        if (!SecureFiles.OwnNewFilesAsAdministrators()) Log.Warning("New files won't be Administrators' if this PC's policy says otherwise");
        if (Program.VelopackStartError is { } velopackError)
            Log.Warning(velopackError, "Velopack start-up failed");
        // before the first window, the update one included, so nothing opens in the other theme first
        AppTheme.Apply(AppTheme.FromSettingsFile(SettingsService.DefaultPath));

        // first, while nothing else runs, so an update can restart the app straight away. Closing the update window or
        // the start-up error card mustn't end the app (last window closed)
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (await SelfUpdate.RunAsync(e.Args, noAutoLogin: shiftAtStart))
        {
            Shutdown(0);
            return;
        }

        // admin-only before any service starts, or a user-level program could plant hook files or hold a lock
        try
        {
            HookFiles.Dir = SecureFiles.SubDir("hook");
            AdminLock.Dir = SecureFiles.SubDir("locks");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Could not prepare the protected folder for the hook's files");
            var text = "Provenance couldn't set up its folder in ProgramData. Close any program that's using that " +
                       "folder, then start Provenance again.";
            try { MessageDialog.ShowError(null, "Provenance can't start", text, offerLogs: true); }
            catch (Exception dialogError)
            {
                Log.Error(dialogError, "The start-up error card could not be shown");
                MessageBox.Show(text, "Provenance", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown(1);
            return;
        }
        // a snapshot holds friends' messages, so it goes once its pol.exe has
        int swept = HookFiles.Sweep(HookFiles.Dir, ProcessExists, TimeSpan.FromDays(7), DateTime.UtcNow);
        if (swept > 0) Log.Information("Removed {Count} old hook file(s)", swept);

        LargeAddressAwareSwitch.OnUiThread = run =>
        {
            if (Current?.Dispatcher is { } ui && !ui.CheckAccess()) ui.InvokeAsync(run);
            else run();
        };
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                // a factory, so DI never picks one of the other constructors
                services.AddSingleton<ICredentialService>(_ => new CredentialService());
                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<FeedService>();
                services.AddSingleton<PolSessionService>();
                services.AddSingleton<AvatarService>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
        await _host.StartAsync();

        // every key's read on a worker now, so the first use on the UI thread finds it ready
        _ = (Services.GetRequiredService<ICredentialService>() as CredentialService)?.OpenKeyInBackground();
        _ = Services.GetRequiredService<PolSessionService>().OpenKeyInBackground();
        _ = OtpSources.OpenKeyInBackground();
        _ = OtpReuse.OpenKeyInBackground();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception");
            args.Handled = true;
            if (_showingError) return;
            _showingError = true;
            try
            {
                if (KeyUnavailable(args.Exception) is { } keyProblem)
                    MessageDialog.ShowError(MainWindow, "Can't open your saved login details", keyProblem.Message,
                                            offerLogs: true);
                else
                    MessageDialog.ShowError(MainWindow, "Provenance ran into a problem",
                        "It's still running, but if something isn't working, close Provenance and open it again. Press Open logs to see what went wrong.",
                        offerLogs: true);
            }
            catch
            {
                MessageBox.Show(UnexpectedText, "Provenance", MessageBoxButton.OK, MessageBoxImage.Error);
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

        // the friends windows are unowned, so the app ends with the main window (the tray only hides it)
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var mainWindow = Services.GetRequiredService<MainWindow>();
        var mainVm = Services.GetRequiredService<MainViewModel>();
        // after the view model's listening for a failed save, before its start-up clears the records of gone pol.exes or
        // pre-starts one
        PolSessionService.NoteScreenSaver(Services.GetRequiredService<ISettingsService>());
        // Windows logging off cancels a sign-in, and WPF would close the window ignoring its Cancel, so it closes the normal
        // way first: a held session's logged out and nothing goes to the tray
        SessionEnding += (_, _) =>
        {
            Log.Information("Windows session ending");
            _ = mainVm.CancelSignInAsync();
            mainWindow.ExitWithoutAsking();
        };
        mainWindow.DataContext = mainVm;
        MainWindow = mainWindow;
        mainWindow.Show();
        if (Services.GetRequiredService<ISettingsService>().Current.MinimizeToTray) TrayIcon.Warm();
        if (SelfUpdate.InstalledCopy) CloseRequest.Listen(() => Dispatcher.InvokeAsync(mainWindow.ExitWithoutAsking));

        bool suppressed = shiftAtStart || ShiftDown()
                          || e.Args.Any(a => a.Equals("--noautologin", StringComparison.OrdinalIgnoreCase));
        _ = Task.Run(() => Provenance.Common.Feeds.FeedClient.PruneImages(TimeSpan.FromDays(60)));
        _ = mainVm.LoadFeedsAsync();
        _ = mainVm.LoadWorldsAsync();
        mainVm.Startup(suppressed);
    }

    private const string UnexpectedText =
        "Provenance ran into a problem it didn't expect, but it's still running. If something isn't working, close " +
        "Provenance and open it again.";

    // the async state as well, a thread that hasn't read any input yet can miss a key already held
    private static bool ShiftDown() =>
        Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) || (GetAsyncKeyState(VkShift) & 0x8000) != 0;

    private const int VkShift = 0x10;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    internal static CredentialKeyUnavailableException? KeyUnavailable(Exception ex) => ex switch
    {
        CredentialKeyUnavailableException key => key,
        AggregateException { InnerExceptions: [CredentialKeyUnavailableException key] } => key,
        _ => null,
    };

    // one that runs but can't be read counts as running, so its files stay
    private static bool ProcessExists(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            try { return !p.HasExited; }
            catch { return true; }
        }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }

    // No await here: WPF kills the dispatcher when OnExit returns, so a held session would never get signed out. Every
    // wait is time-limited on the pool.
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("Provenance exiting (code {Code})", e.ApplicationExitCode);
            try { Task.Run(OtpSources.LockBitwardenAsync).Wait(TimeSpan.FromSeconds(5)); } catch { }
            if (_host is { } host) EndHost(host);
            if (RestartOnExit) StartAgain();
        }
        finally
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

    // after the host's gone, so a held session's already logged out by the time the new copy starts
    private static void StartAgain()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            using var started = System.Diagnostics.Process.Start(psi);
            Log.Information("Started Provenance again (pid {Pid})", started?.Id);
        }
        catch (Exception ex) { Log.Error(ex, "Could not start Provenance again"); }
    }

    internal static void EndHost(IHost host)
    {
        try { Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(2))).Wait(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { Log.Warning(ex, "Stopping the host failed"); }
        try { host.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "Disposing the host failed"); }
    }
}
