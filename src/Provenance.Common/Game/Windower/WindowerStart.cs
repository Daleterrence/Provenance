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
    private WindowerBackgroundUpdate? _update;

    public WindowerStart(ISettingsService settings, Watcher watcher, WindowerSetup? setup = null)
    {
        _watcher = watcher;
        _setup = setup ?? ((polExe, profile, lists, shared) => WindowerLauncher.SetupFor(settings, polExe, profile, lists, shared));
    }

    internal WindowerBackgroundUpdate? Update => Volatile.Read(ref _update);

    internal void Forget()
    {
        Volatile.Write(ref _play, null);
        Volatile.Write(ref _update, null);
    }

    internal int? Start(ProcessStartInfo psi, string polExe, string launchId, SessionOptions options, IProgress<LaunchProgress>? progress, CancellationToken ct,
                        WindowerUpdateAnswers? answers = null)
    {
        WindowerLauncher windower;
        try
        {
            var setup = _setup(polExe, options.ThirdPartyHookProfile, options.WindowerLists, options.WindowerShared) with { LeaveOut = options.WindowerLeaveOut };
            windower = WindowerLauncher.Prepare(answers?.Value is { } known ? setup with { KnownUpdate = known } : setup, progress, ct);
            if (answers is not null) answers.Value ??= windower.UpdateAnswer;
            Volatile.Write(ref _update, windower.Background);
        }
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

    internal void PutValuesBack()
    {
        if (Volatile.Read(ref _play) is not { } values) return;
        try { Log.Information("Windower's game settings for {Profile} written again before Play ({Keys})", values.Profile.Name.Length > 0 ? values.Profile.Name : "Default (no name)", values.Apply().Count); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warning(ex, "Could not write Windower's game settings again before Play");
        }
    }
}
