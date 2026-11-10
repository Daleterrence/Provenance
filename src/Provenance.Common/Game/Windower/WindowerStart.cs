using System.Diagnostics;
using Provenance.Common.Login;
using Provenance.Common.Settings;
using Serilog;

namespace Provenance.Common.Game.Windower;

internal delegate WindowerLaunchSetup WindowerSetup(string polExe, string? profile,
    IReadOnlyDictionary<string, WindowerAddonList>? lists, WindowerAddonList? shared);

internal sealed class WindowerStart
{
    private readonly Watcher _watcher;
    private readonly WindowerSetup _setup;
    private WindowerGameValues? _play;

    public WindowerStart(ISettingsService settings, Watcher watcher, WindowerSetup? setup = null)
    {
        _watcher = watcher;
        _setup = setup ?? ((polExe, profile, lists, shared) => WindowerLauncher.SetupFor(settings, polExe, profile, lists, shared));
    }

    internal void Forget() => Volatile.Write(ref _play, null);

    internal int? Start(ProcessStartInfo psi, string polExe, string launchId, SessionOptions options, IProgress<LaunchProgress>? progress, CancellationToken ct)
    {
        WindowerLauncher windower;
        try { windower = WindowerLauncher.Prepare(_setup(polExe, options.ThirdPartyHookProfile, options.WindowerLists, options.WindowerShared), progress, ct); }
        catch (OperationCanceledException)
        {
            progress?.Report(LaunchProgress.Cancelled());
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Windower wasn't ready to start the game");
            progress?.Report(LaunchProgress.Failed(ErrorText.Of(ex, "Provenance couldn't get Windower ready to start the game. Try again in a moment.")));
            return null;
        }
        // held till the watcher is done, Hook.dll copies the mapping out before it signals
        using (windower)
        {
            Watcher.SetArguments(psi, windower.Arguments(launchId));
            HookEnvironment.SetLaunchId(psi.Environment, launchId);
            progress?.Report(new LaunchProgress("Starting", "Starting PlayOnline with Windower...", 0.02));
            var pid = _watcher.Start(psi, progress, ct, WindowerLauncher.WatcherLimit, Watcher.WindowerDidNotStart);
            if (pid is not null) Volatile.Write(ref _play, windower.GameValues);
            return pid;
        }
    }

    // another launch through Windower may have written its own profile's values since this one's
    internal void PutValuesBack()
    {
        if (Volatile.Read(ref _play) is not { } values) return;
        try { Log.Information("Windower's game settings for {Profile} written again before Play ({Keys})", values.Profile.Name, values.Apply().Count); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warning(ex, "Could not write Windower's game settings again before Play");
        }
    }
}
