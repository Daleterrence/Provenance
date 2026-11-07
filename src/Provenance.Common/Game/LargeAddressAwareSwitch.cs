using Provenance.Common.Install;
using Provenance.Common.Patcher;
using Provenance.Common.Settings;
using Serilog;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Game;

// pol.exe only changes under the update lock with no pol.exe from the viewer running. The setting's what the switch
// shows, the file just catches up with it
public static class LargeAddressAwareSwitch
{
    public sealed record Reading(bool Available, string? Note);

    public sealed record Target(string? ViewerDir, Func<bool> PolRunning, Func<string?> Blocked, Func<string?> Busy);

    public enum Outcome { Done, NextStart, Denied }

    internal const string UnsupportedText = "Provenance can't apply this to your version of the PlayOnline Viewer.";
    public const string LoggedInText = "You can change this once you're logged out of PlayOnline.";
    public const string MultiRunText = "You can change this once Provenance has finished starting your accounts.";
    internal static string LockedText(GameFilesLock.Job? holder) =>
        $"Another copy of Provenance is {GameFilesLock.Doing(holder)}. You can change this once it's finished.";
    internal const string OnNextStartText = "Provenance will turn this on the next time the game starts.";
    internal const string OffNextStartText = "Provenance will turn this off the next time the game starts.";
    public const string DeniedText = "Windows won't let Provenance change the PlayOnline Viewer's files, so this setting can't be applied.";
    public const string WaitText = "Hang on a moment while Provenance changes this setting.";

    private static readonly TimeSpan LockWait = TimeSpan.FromMilliseconds(500);
    private static volatile bool _lastWriteDenied;

    public static Target For(PolRegion? region, Func<string?>? blocked = null) =>
        new(region is { } r ? PolPaths.TryGetInstallFolder(r, GameProduct.Viewer) : null,
            PolUpdates.ViewerRunning, blocked ?? (() => null), Busy);

    private static string? Busy()
    {
        try { return GameFilesLock.LockFree() ? null : LockedText(GameFilesLock.Holder()); }
        catch (Exception e)
        {
            Log.Debug(e, "Could not check whether Large Address Aware could be changed");
            return null;
        }
    }

    public static Reading Read(Target t, bool on, bool clearAtStart) =>
        (t.Blocked() ?? t.Busy()) is { } why ? new(false, why) : new(true, Note(t.ViewerDir, on, clearAtStart));

    private static string? Note(string? viewerDir, bool on, bool clearAtStart)
    {
        if (viewerDir is null || !File.Exists(Path.Combine(viewerDir, GameProduct.Viewer.KeyFile))) return null;
        var polExe = Path.Combine(viewerDir, GameProduct.Viewer.KeyFile);
        try
        {
            if (!LargeAddressAware.IsKnownBuild(polExe))
                return !on ? null : SignedBuilds.Check(viewerDir) == ViewerBuild.State.Outdated ? OnNextStartText : UnsupportedText;
            var bit = LargeAddressAware.Read(polExe);
            bool behind = on ? bit == false : clearAtStart && bit == true;
            return !behind ? null : _lastWriteDenied ? DeniedText : on ? OnNextStartText : OffNextStartText;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Large Address Aware: could not read the viewer files");
            return null;
        }
    }

    public static Outcome Change(Target t, bool on, Action? holdPrestarts, Action? releasePrestarts)
    {
        if (t.ViewerDir is not { } dir || !File.Exists(Path.Combine(dir, GameProduct.Viewer.KeyFile))) return Outcome.Done;
        var polExe = Path.Combine(dir, GameProduct.Viewer.KeyFile);
        try
        {
            if (LargeAddressAware.Read(polExe) == on) return Outcome.Done;
            // the next login's own update brings a build Provenance knows
            if (!LargeAddressAware.IsKnownBuild(polExe))
                return SignedBuilds.Check(dir) == ViewerBuild.State.Outdated ? Outcome.NextStart : Outcome.Done;
            if (t.Blocked() is not null) return Outcome.NextStart;
            using var gate = GameFilesLock.TryLock(GameFilesLock.Job.LargeAddressAware, TimeSpan.FromSeconds(2));
            // again under the lock, a login starting now would want my waiting pol.exe
            if (gate is null || t.Blocked() is not null) return Outcome.NextStart;
            if (LargeAddressAware.Read(polExe) == on) return Outcome.Done;
            var result = TryWrite(t, on, holdPrestarts, releasePrestarts, retryInUse: false);
            Log.Information("Large Address Aware {Done} {State} in pol.exe: {Result}", result == LargeAddressAware.Result.Done ? "turned" : "not turned",
                            on ? "on" : "off", result?.ToString() ?? "a pol.exe was running");
            return result switch
            {
                LargeAddressAware.Result.Done or LargeAddressAware.Result.NotPe or LargeAddressAware.Result.UnknownBuild => Outcome.Done,
                LargeAddressAware.Result.Denied => Outcome.Denied,
                _ => Outcome.NextStart,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Large Address Aware: could not change pol.exe");
            return Outcome.NextStart;
        }
    }

    public static void Apply(ISettingsService settings, string when, Action? holdPrestarts = null, Action? releasePrestarts = null,
                             Func<string?>? blocked = null)
    {
        if (PolPaths.Supported(settings.DetectedRegion))
            Apply(settings, For(settings.DetectedRegion, blocked), when, holdPrestarts, releasePrestarts);
    }

    // anything in the way gets logged and the start carries on without it
    internal static void Apply(ISettingsService settings, Target t, string when, Action? holdPrestarts = null, Action? releasePrestarts = null)
    {
        try
        {
            // another copy may have turned it off since this one loaded its settings
            var s = settings.ReadSaved() ?? settings.Current;
            bool on = s.LargeAddressAware;
            if ((!on && !s.LargeAddressAwareClearAtStart) || t.ViewerDir is not { } dir) return;
            var polExe = Path.Combine(dir, GameProduct.Viewer.KeyFile);
            if (LargeAddressAware.Read(polExe) == on)
            {
                if (!on) ClearDone(settings);
                return;
            }
            using var gate = GameFilesLock.TryLock(GameFilesLock.Job.LargeAddressAware, LockWait);
            if (gate is null) { Log.Information("Large Address Aware not applied {When}: an update or repair is running", when); return; }
            if (LargeAddressAware.Read(polExe) == on)
            {
                if (!on) ClearDone(settings);
                return;
            }
            if (t.Blocked() is { } blocked) { Log.Information("Large Address Aware not applied {When}: {Reason}", when, blocked); return; }
            var result = TryWrite(t, on, holdPrestarts, releasePrestarts, retryInUse: true);
            if (result is LargeAddressAware.Result.UnknownBuild or LargeAddressAware.Result.NotPe)
                Log.Information("Large Address Aware not applied {When}: pol.exe is a build Provenance doesn't know, so it was left alone", when);
            else
                Log.Information("Large Address Aware {Done} {State} in pol.exe {When}: {Result}",
                                result == LargeAddressAware.Result.Done ? "turned" : "not turned", on ? "on" : "off", when,
                                result?.ToString() ?? "a pol.exe from the viewer is running");
            if (result == LargeAddressAware.Result.Done && !on) ClearDone(settings);
        }
        catch (Exception e) { Log.Warning(e, "Large Address Aware not applied {When}", when); }
    }

    // a fresh pol.exe from an update tends to get a look from the AV straight away, so a start retries while it's in use
    private static LargeAddressAware.Result? TryWrite(Target t, bool on, Action? hold, Action? release, bool retryInUse)
    {
        bool held = false;
        try
        {
            if (hold is not null)
            {
                held = true;
                hold();
                for (int i = 0; i < 10 && t.PolRunning(); i++) Thread.Sleep(300);
            }
            if (t.PolRunning()) return null;
            var polExe = Path.Combine(t.ViewerDir!, GameProduct.Viewer.KeyFile);
            var result = LargeAddressAware.Set(polExe, on);
            for (int i = 0; retryInUse && i < 4 && result == LargeAddressAware.Result.InUse; i++)
            {
                Thread.Sleep(100);
                result = LargeAddressAware.Set(polExe, on);
            }
            if (result == LargeAddressAware.Result.Denied) _lastWriteDenied = true;
            else if (result == LargeAddressAware.Result.Done) _lastWriteDenied = false;
            return result;
        }
        finally
        {
            if (held && release is not null)
            {
                try { release(); }
                catch (Exception e) { Log.Warning(e, "Could not let pre-starts go again after Large Address Aware"); }
            }
        }
    }

    // a start runs on a worker, and settings only change on the thread that owns them
    private static void ClearDone(ISettingsService settings)
    {
        settings.OnOwnerThread(() => settings.SaveChange(s =>
        {
            if (!s.LargeAddressAware && s.LargeAddressAwareClearAtStart) s.LargeAddressAwareClearAtStart = false;
        }));
    }
}
