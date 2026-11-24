using System.Windows;
using System.Windows.Input;
using Provenance.Common.Credentials;
using Provenance.Common.Feeds;
using Provenance.Common.Game;
using Provenance.Common.Game.Windower;
using Provenance.Common.Hook;
using Provenance.Common.Logging;
using Provenance.Common.Login;
using Provenance.Common.Otp;
using Provenance.Common.Patcher;
using Provenance.Common.Security;
using Provenance.Common.Settings;
using Provenance.Common.Social;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.Win32;
using Serilog;
using Provenance.Wpf.Services;
using Provenance.Wpf.Views.Main;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf;

public partial class App : Application
{
    static App()
    {
        FocusLook.Install();
        DropdownLists.Install();
    }

    private PolSession? _pol;
    private MainViewModel? _main;
    private bool _showingError;
    private bool _started;

    internal const string StartupFailedText =
        "Provenance ran into a problem while starting up. Press Open logs to see what went wrong, then start Provenance again.";

    internal static bool RestartOnExit { get; set; }

    internal static bool RestartForUpdate { get; set; }

    internal static bool SkipStartup { get; set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (SkipStartup) return;
        base.OnStartup(e);
        var args = new LaunchArgs(e.Args);
        bool shiftAtStart = ShiftDown() || args.ShiftHeld;
        CatchUnhandled();
        Log.Information("Provenance starting");
        AppTheme.Apply(AppTheme.FromSettingsFile(SettingsService.DefaultPath));

        if (!args.AfterUpdate && TrayCopy.ReuseRunning())
        {
            Shutdown(0);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (await SelfUpdate.RunAsync(args, noAutoLogin: shiftAtStart))
        {
            Shutdown(0);
            return;
        }

        // admin-only before any service starts, or a user-level program could plant hook files
        if (!PrepareProtectedFolders())
        {
            Shutdown(1);
            return;
        }
        if (!BuildServices(out var credentials, out var settings, out var pol))
        {
            Shutdown(1);
            return;
        }

        WarmKeys();
        MirrorCheck.StartAtOpen(settings);
        WindowerLauncher.CheckAtOpen(settings);
        bool shift = AutoLoginAtOpen.ShiftAtStart(shiftAtStart, ShiftDown, settings);
        var autoLogin = AutoLoginAtOpen.Coming(settings, credentials, shift || args.NoAutoLogin);
        StartPolEarly(() => ScreenSaver.Note(settings), pol.Records.EndOrphans, () => pol.Prestarter.StartEarly(loginComing: autoLogin is not null));
        AutoLoginAtOpen.StartAhead(pol, autoLogin);

        shift = AutoLoginAtOpen.ShiftLater(shift, ShiftDown, settings);
        bool noAutoLogin = shift || args.NoAutoLogin;
        var mainVm = ShowMainWindow(credentials, settings, pol, noAutoLogin);
        _ = SelfUpdate.TellIfUpdateWaiting(mainVm.UpdateNotice);
        SelfUpdate.TellIfHeldBack(args, mainVm.Notice);
        StartMainViewModel(mainVm, noAutoLogin);
        _ = Dispatcher.InvokeAsync(() => LogsNotice.Tell(WpfDialogs.Default, Program.LogsProblem));
    }

    internal static void StartPolEarly(Action noteScreenSaver, Action endOrphans, Action startPol)
    {
        noteScreenSaver();
        foreach (var (what, step) in new[] { ("ending the sessions an earlier copy left", endOrphans), ("starting pol.exe", startPol) })
        {
            try { step(); }
            catch (Exception ex) { Log.Warning(ex, "Start-up carried on after {Step} failed", what); }
        }
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

    private bool BuildServices(out CredentialService credentials, out SettingsService settings, out PolSession pol)
    {
        credentials = null!;
        settings = null!;
        pol = null!;
        try
        {
            SweepOldHookFiles();
            var uiThread = SynchronizationContext.Current;
            if (uiThread is null) Log.Warning("The settings were built with no UI thread to hand changes to");
            credentials = new CredentialService();
            settings = new SettingsService(SettingsService.DefaultPath, uiThread);
            LogStartupLine(settings);
            HookFiles.SetGameSound(settings.Current.GameMessageSound);
            HookFiles.SetGameFriendLines(settings.Current.GameFriendLines);
            HookFiles.SetGamePlayingLines(settings.Current.GamePlayingLines);
            pol = _pol = new PolSession(settings);
            return true;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Could not set up Provenance's settings and services");
            StartupErrors.Show("Provenance can't start", StartupFailedText, offerLogs: true);
            return false;
        }
    }

    private static void LogStartupLine(SettingsService settings)
    {
        try
        {
            var region = settings.DetectedRegion ?? FeedService.Resolve(settings.Current.FeedRegion, null);
            Log.Information("{Line:l}", StartupLine.For(VersionTag.OwnText() ?? "dev", Environment.OSVersion.Version,
                settings.Current.ThirdPartyHookPlatform, region));
        }
        catch (Exception ex) { Log.Warning(ex, "Couldn't log what Provenance is running on"); }
    }

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
                if (!_started)
                {
                    Log.Fatal("Start-up ended on the exception above");
                    StartupErrors.Show("Provenance can't start", StartupFailedText, offerLogs: true);
                    Shutdown(1);
                }
                else if (KeyUnavailable(args.Exception) is { } keyProblem)
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
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.Information("Process exit");
    }

    private MainViewModel ShowMainWindow(CredentialService credentials, SettingsService settings, PolSession pol, bool noAutoLogin)
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var timer = new StepTimer("Main window");
        var mainWindow = new MainWindow();
        timer.Lap("window built");
        var windows = new WindowManager(settings, WpfDialogs.Default);
        var mainVm = _main = new MainViewModel(WpfDialogs.Default, windows, credentials, settings, new FeedService(settings), pol, new AvatarService(settings, needExtras: Provenance.Common.PolFormat.ProfilePictureExtras.Need));
        timer.Lap("view model built");
        // Windows logging off cancels a login, and WPF would close the window ignoring its Cancel, so it closes normally first
        SessionEnding += (_, e) =>
        {
            if (LogOff(mainVm, mainWindow)) e.Cancel = true;
        };
        mainWindow.DataContext = mainVm;
        MainWindow = mainWindow;
        _started = true;
        OpenMainWindow(mainWindow, mainVm, noAutoLogin);
        timer.Lap("shown");
        if (settings.Current.MinimizeToTray) TrayIcon.Warm();
        if (SelfUpdate.InstalledCopy) CloseRequest.Listen(() => Dispatcher.InvokeAsync(mainWindow.ExitWithoutAsking));
        return mainVm;
    }

    internal static void OpenMainWindow(Window mainWindow, MainViewModel mainVm, bool noAutoLogin)
    {
        if (mainVm.StartsLean(noAutoLogin)) mainVm.News.SetWindowShown(false);
        else mainWindow.Show();
    }

    internal static void ShowIfNothingStarted(Window mainWindow, MainViewModel mainVm)
    {
        if (!mainWindow.IsVisible && !mainWindow.IsLoaded && !mainVm.IsLaunching) mainWindow.Show();
    }

    internal static bool LogOff(MainViewModel mainVm, MainWindow mainWindow)
    {
        Log.Information("Windows session ending");
        _ = mainVm.CancelLogInAsync();
        bool refuse = mainWindow.ExitWaitsForAnswer;
        mainWindow.ExitWithoutAsking();
        return refuse;
    }

    private void StartMainViewModel(MainViewModel mainVm, bool suppressed)
    {
        _ = Task.Run(() => Provenance.Common.Feeds.BannerImages.Prune(TimeSpan.FromDays(60)));
        if (mainVm.News.WindowShown)
        {
            _ = mainVm.News.LoadFeedsAsync();
            _ = mainVm.News.LoadWorldsAsync();
        }
        mainVm.Social.ZoneSearch = FriendZoneSearch.Retail();
        mainVm.Startup(suppressed);
        if (MainWindow is { } mainWindow) ShowIfNothingStarted(mainWindow, mainVm);
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

    // no await, WPF kills the dispatcher when OnExit returns and a held session would never get logged out
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("Provenance exiting (code {Code})", e.ApplicationExitCode);
            _main?.SaveMessages();
            if (_pol is { } pol) EndSession(pol);
            try { Task.Run(Bitwarden.LockAtExitAsync).Wait(TimeSpan.FromSeconds(5)); } catch { }
            WindowerLauncher.StopBackgroundUpdates(TimeSpan.FromSeconds(3));
            if (SelfUpdate.AfterExit(RestartOnExit, RestartForUpdate) is { } startWith) StartAgain(startWith);
        }
        finally
        {
            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

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

    internal static void EndSession(IDisposable session)
    {
        try { session.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "Ending the PlayOnline session failed"); }
    }
}
