using System.Diagnostics;
using System.Globalization;
using System.IO;
using Serilog;
using Provenance.Common.Game;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.Services;

// the automatic backup of the game's own settings (USER, its VirtualStore copy and the term filter lists), made right
// before the game starts
public static class GameBackups
{
    internal static TimeSpan PlayWait = TimeSpan.FromSeconds(5);
    internal static TimeSpan RestoreWait = TimeSpan.FromSeconds(10);

    internal const string WorkingText = "Backing up your game settings...";

    public static BackupSources? SourcesFor(LauncherSettings s, PolRegion region, string? accountBoot) =>
        PolPaths.TryGetInstallFolder(region, PolPaths.FfxiProduct) is { } ffxi
            ? GameSettingsBackup.Sources(ffxi, SettingsFolder(s, new RegistryGameStore(region), accountBoot))
            : null;

    // 0042 as the game will see it, which through Ashita is the boot configuration's own when it sets one
    internal static string SettingsFolder(LauncherSettings s, RegistryGameStore registry, string? accountBoot)
    {
        var windows = registry.LoadWindows().ScreenshotFolder;
        if ((s.ThirdPartyHookPlatform ?? "").Trim().ToLowerInvariant() != ThirdPartyHook.Ashita) return windows;
        var dir = AshitaInstall.DetectAshita(s.AshitaPath);
        var boot = accountBoot is { Length: > 0 } ? accountBoot
                 : string.IsNullOrWhiteSpace(s.AshitaBootConfig) ? AshitaInstall.LastUsedAshitaBootConfig(dir) : s.AshitaBootConfig;
        if (dir is null || boot is not { Length: > 0 } || !ThirdPartyHookLauncher.IsBootFileName(boot)) return windows;
        try { return new AshitaGameStore(dir, boot, registry).Load().ScreenshotFolder; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Game settings backup: couldn't read the settings folder from {Boot}, using Windows' one", boot);
            return windows;
        }
    }

    // what the player's told, or null when there's nothing to say. Never throws, the game starts whatever happens here
    internal static string? BeforePlay(LauncherSettings s, Func<BackupSources?> sources)
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
            Log.Information("Game settings backup: {Outcome} {Zip} in {Ms} ms", result.Outcome, result.Zip, took.ElapsedMilliseconds);
            foreach (var line in result.LeftOut) Log.Information("Game settings backup: {Line}", line);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Game settings backup failed");
            return FailedText(ex);
        }
    }

    internal const string NotOursText = "That file isn't one of Provenance's game settings backups, or it's damaged, so nothing was restored.";
    internal const string BusyText = "Another copy of Provenance is backing up your game settings. Try again in a moment.";
    internal const string PartlyText = "Not all of the settings could be put back. Your newest backup has them as they were before, so you can restore that one to undo this.";

    private static readonly CultureInfo British = CultureInfo.GetCultureInfo("en-GB");

    internal static string When(BackupInfo b) => b.CreatedUtc.ToLocalTime().ToString("d MMMM yyyy 'at' HH:mm", British);

    // on a worker, the lock's held by this thread. What's shown afterwards, and whether it worked
    internal static (bool Done, string Text) Restore(string zip, BackupInfo info, BackupSources sources, string folder, Func<string?> blocked)
    {
        try
        {
            using var gate = GameSettingsBackup.TryLock(RestoreWait);
            if (gate is null) return (false, BusyText);
            var took = Stopwatch.StartNew();
            var r = GameSettingsBackup.Restore(zip, sources, folder, blocked);
            Log.Information("Restored {Count} files from {Zip} in {Ms} ms, backed up first: {Outcome} {Before}", r.Restored, zip,
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
        catch (Exception e) when (ErrorText.Ours(e)) { return (false, e.Message); }
        catch (Exception e)
        {
            Log.Error(e, "Restore from {Zip} failed part way", zip);
            return (false, PartlyText);
        }
    }

    internal static string FailedText(Exception ex) => Reason(ex) is { } r
        ? $"Your game settings couldn't be backed up, because {r.Why}. {r.Fix}"
        : "Your game settings couldn't be backed up because of a problem Provenance didn't expect. It'll try again the next time you play.";

    private const int DiskFull = unchecked((int)0x80070070), HandleDiskFull = unchecked((int)0x80070027),
                      NotReady = unchecked((int)0x80070015), BadNetPath = unchecked((int)0x80070035), BadNetName = unchecked((int)0x80070043);

    // only the backups folder can stop a backup, a source file that won't read is just left out
    internal static (string Why, string Fix)? Reason(Exception ex) => ex switch
    {
        BackupFolderLinkedException => ("your backups folder is a link to somewhere else", "Choose the folder it points to in Settings, under General."),
        IOException { HResult: DiskFull or HandleDiskFull } => ("the drive your backups folder is on is full", "Free up some space on it, or choose another folder in Settings, under General."),
        UnauthorizedAccessException => ("Windows didn't let Provenance save them in your backups folder", "Choose another folder in Settings, under General."),
        DirectoryNotFoundException or DriveNotFoundException or IOException { HResult: NotReady or BadNetPath or BadNetName } =>
            ("your backups folder can't be reached", "Check its drive is connected, or choose another folder in Settings, under General."),
        _ => null,
    };
}
