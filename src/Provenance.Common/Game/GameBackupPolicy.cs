using System.Diagnostics;
using System.Globalization;
using Provenance.Common.Game.Ashita;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Install;
using Provenance.Common.Settings;
using Serilog;
using Provenance.Common.PolFormat;

namespace Provenance.Common.Game;

public static class GameBackupPolicy
{
    internal static TimeSpan PlayWait = TimeSpan.FromSeconds(5);
    internal static TimeSpan RestoreWait = TimeSpan.FromSeconds(10);

    public const string WorkingText = "Backing up your game settings...";

    public static BackupSources? SourcesFor(LauncherSettings s, PolRegion region, string? accountBoot, ThirdPartyHook? platform = null) =>
        PolPaths.TryGetInstallFolder(region, GameProduct.Game) is { } ffxi
            ? GameSettingsBackup.Sources(ffxi, SettingsFolder(s, new RegistryGameStore(region), accountBoot, platform))
            : null;

    internal static string SettingsFolder(LauncherSettings s, RegistryGameStore registry, string? accountBoot, ThirdPartyHook? platform = null)
    {
        var windows = registry.LoadWindows().ScreenshotFolder;
        if ((platform ?? s.ThirdPartyHookPlatform) != ThirdPartyHook.Ashita) return windows;
        var play = PlayTarget.For(s, accountBoot, ThirdPartyHook.Ashita);
        var (dir, boot) = (play.Dir, play.Profile);
        if (dir is null || boot.Length == 0 || !AshitaBootFiles.IsBootFileName(boot)) return windows;
        try { return new AshitaGameStore(dir, boot, registry).Load().ScreenshotFolder; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Game settings backup: couldn't read the settings folder from {Boot}, using Windows' one", boot);
            return windows;
        }
    }

    public static string? BeforePlay(LauncherSettings s, Func<BackupSources?> sources)
    {
        try
        {
            if (sources() is not { } from)
            {
                Log.Information("Game settings backup: no FINAL FANTASY XI install to back up");
                return null;
            }
            using var gate = GameSettingsBackup.TryLock(PlayWait);
            if (gate is null)
            {
                Log.Information("Game settings backup skipped: another copy of Provenance was still making one");
                return null;
            }
            var took = Stopwatch.StartNew();
            var result = GameSettingsBackup.BackUp(from, GameSettingsBackup.FolderOrDefault(s.BackupFolder));
            Log.Information("Game settings backup: {Outcome} {Zip} in {Ms}ms", result.Outcome, result.Zip, took.ElapsedMilliseconds);
            foreach (var line in result.LeftOut) Log.Information("Game settings backup: {Line}", line);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Game settings backup failed");
            return FailedText(ex);
        }
    }

    public const string NotOursText = "That file isn't one of Provenance's game settings backups, or it's damaged, so nothing was restored.";
    internal const string BusyText = "Another copy of Provenance is backing up your game settings. Try again in a moment.";
    internal const string PartlyNoBackupText = "Not all of the settings could be put back, and there were no settings to back up first.";
    internal const string PartlyText = "Not all of the settings could be put back. Your newest backup has them as they were before, so you can restore that one to undo this.";

    private static readonly CultureInfo British = CultureInfo.GetCultureInfo("en-GB");

    public static string When(BackupInfo b) => b.CreatedUtc.ToLocalTime().ToString("d MMMM yyyy 'at' HH:mm", British);

    public static (bool Done, string Text) Restore(string zip, BackupInfo info, BackupSources sources, string folder, Func<string?> blocked)
    {
        try
        {
            using var gate = GameSettingsBackup.TryLock(RestoreWait);
            if (gate is null) return (false, BusyText);
            var took = Stopwatch.StartNew();
            var r = GameSettingsBackup.Restore(zip, sources, folder, blocked);
            Log.Information("Restored {Count} files from {Zip} in {Ms}ms, backed up first: {Outcome} {Before}", r.Restored, zip,
                            took.ElapsedMilliseconds, r.Before.Outcome, r.Before.Zip);
            foreach (var line in r.LeftOut) Log.Information("Restore: {Line}", line);
            return (true, $"Your game settings from the backup made on {When(info)} are back.");
        }
        catch (NotABackupException) { return (false, NotOursText); }
        catch (BackupBeforeRestoreException e)
        {
            Log.Warning(e.InnerException, "Restore: the current settings couldn't be backed up first, nothing was restored");
            return (false, Reason(e.InnerException!) is { } r
                ? $"Your current game settings couldn't be backed up first, because {r.Why}, so nothing was restored. {r.Fix}"
                : "Your current game settings couldn't be backed up first, so nothing was restored. Try again in a moment.");
        }
        catch (RestoredPartlyException e)
        {
            Log.Error(e.InnerException, "Restore from {Zip} failed part way", zip);
            return (false, e.Before == BackupOutcome.NothingThere ? PartlyNoBackupText : PartlyText);
        }
        catch (Exception e) when (ErrorText.Ours(e)) { return (false, e.Message); }
        catch (Exception e)
        {
            Log.Error(e, "Restore from {Zip} failed", zip);
            return (false, "Your game settings couldn't be put back because of a problem Provenance didn't expect.");
        }
    }

    public const string BackedUpText = "Your game settings are backed up.";
    public const string UnchangedText = "Your game settings haven't changed since your newest backup, so there's nothing new to back up.";
    public const string NothingThereText = "There were no game settings to back up.";

    public static (bool Done, string Text) BackUpNow(BackupSources sources, string folder)
    {
        try
        {
            using var gate = GameSettingsBackup.TryLock(RestoreWait);
            if (gate is null) return (false, BusyText);
            var result = GameSettingsBackup.BackUp(sources, folder);
            Log.Information("Game settings backup (asked for): {Outcome} {Zip}", result.Outcome, result.Zip);
            foreach (var line in result.LeftOut) Log.Information("Game settings backup: {Line}", line);
            return result.Outcome switch
            {
                BackupOutcome.Unchanged => (true, UnchangedText),
                BackupOutcome.NothingThere => (true, NothingThereText),
                _ => (true, BackedUpText),
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Game settings backup (asked for) failed");
            return (false, FailedText(ex));
        }
    }

    internal static string FailedText(Exception ex) => Reason(ex) is { } r
        ? $"Your game settings couldn't be backed up, because {r.Why}. {r.Fix}"
        : "Your game settings couldn't be backed up because of a problem Provenance didn't expect. It'll try again the next time you play.";

    private const int DiskFull = unchecked((int)0x80070070), HandleDiskFull = unchecked((int)0x80070027),
                      NotReady = unchecked((int)0x80070015), BadNetPath = unchecked((int)0x80070035), BadNetName = unchecked((int)0x80070043);

    internal static (string Why, string Fix)? Reason(Exception ex) => ex switch
    {
        BackupFolderLinkedException => ("your backups folder is a link to somewhere else", "Choose the folder it points to in Settings, under Backups."),
        IOException { HResult: DiskFull or HandleDiskFull } => ("the drive your backups folder is on is full", "Free up some space on it, or choose another folder in Settings, under Backups."),
        UnauthorizedAccessException => ("Windows didn't let Provenance save them in your backups folder", "Choose another folder in Settings, under Backups."),
        DirectoryNotFoundException or DriveNotFoundException or IOException { HResult: NotReady or BadNetPath or BadNetName } =>
            ("your backups folder can't be reached", "Check its drive is connected, or choose another folder in Settings, under Backups."),
        _ => null,
    };
}
