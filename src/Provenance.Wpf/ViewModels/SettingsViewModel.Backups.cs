using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Provenance.Common.Game;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.ViewModels;

// General's backups card. Like the rest of the page every change saves straight away
public partial class SettingsViewModel
{
    [ObservableProperty] private bool _backupGameSettings;
    [ObservableProperty] private string _backupFolder = "";
    [ObservableProperty] private string _backupStatus = "";
    [ObservableProperty] private bool _backupStatusIsError;

    public string DefaultBackupFolder => GameSettingsBackup.DefaultFolder;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RestoreBackupCommand))] private bool _restoringBackup;

    // a test swaps these for ones that just note what they got
    internal Func<string, string?> PickBackupFolder { get; set; } = PickFolder;
    internal Action<string> ShowBackupFolder { get; set; } = Explorer.ShowFolder;
    internal Func<string, string?> PickBackup { get; set; } = PickZip;
    internal Action<string, string, bool>? ShowRestored { get; set; }
    internal Func<bool> GameRunning { get; set; } = PolUpdates.GameRunning;
    internal Func<LauncherSettings, BackupSources?>? RestoreSourcesForTest { get; set; }

    private const string RestoreTitle = "Restore game settings", RestoreFailedTitle = "Couldn't restore the backup";

    partial void OnBackupGameSettingsChanged(bool value) => Save(s => s.BackupGameSettings = value);

    // an empty box is the default folder
    partial void OnBackupFolderChanged(string value)
    {
        var folder = value.Trim();
        if (folder.Length > 0 && !Path.IsPathFullyQualified(folder))
        {
            SayBackup(@"Type the whole path of a folder, like D:\FFXI backups.", error: true);
            return;
        }
        SayBackup("");
        Save(s => s.BackupFolder = folder);
    }

    [RelayCommand]
    private void BrowseBackupFolder()
    {
        if (PickBackupFolder("Choose a folder for your game settings backups") is { } d) BackupFolder = d;
    }

    // the folder there's no backup in yet gets made, so there's something to open
    [RelayCommand]
    private void OpenBackupFolder()
    {
        var folder = GameSettingsBackup.FolderOrDefault(_settings.Current.BackupFolder);
        try
        {
            Directory.CreateDirectory(folder);
            ShowBackupFolder(folder);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open the backups folder {Folder}", folder);
            SayBackup("Windows couldn't open that folder.", error: true);
        }
    }

    private static string? PickZip(string folder)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a backup to restore",
            Filter = $"Game settings backups|{GameSettingsBackup.NameStart}*.zip",
            InitialDirectory = Directory.Exists(folder) ? folder : "",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    // the game writes its settings when a character logs out, so a restore under a running one wouldn't last. Asked
    // again right before anything's written
    internal string? RestoreBlocked() =>
        GameRunning() ? "FINAL FANTASY XI is running, and it would write over the restored settings when you log out. Close the game, then restore the backup."
        : _loggedIn?.Invoke() == true ? "You can restore a backup once you're logged out of PlayOnline."
        : MultiRunGoing ? "You can restore a backup once Provenance has finished starting your accounts."
        : null;

    private bool CanRestore() => !RestoringBackup;

    internal Task Restoring { get; private set; } = Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreBackup() => Restoring = RestoreBackupAsync();

    private async Task RestoreBackupAsync()
    {
        if (RestoreBlocked() is { } blocked)
        {
            ShowRestored?.Invoke(RestoreFailedTitle, blocked, true);
            return;
        }
        var s = _settings.Current;
        var sources = RestoreSourcesForTest is { } test ? test(s) : _settings.DetectedRegion is { } r ? GameBackups.SourcesFor(s, r, null) : null;
        if (sources is null)
        {
            ShowRestored?.Invoke(RestoreFailedTitle, "FINAL FANTASY XI isn't installed on this PC, so there's nowhere to restore the backup to.", true);
            return;
        }
        var folder = GameSettingsBackup.FolderOrDefault(s.BackupFolder);
        if (PickBackup(folder) is not { } zip) return;
        BackupInfo info;
        try { info = GameSettingsBackup.Check(zip); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Restore: {Zip} isn't a backup that can be restored", zip);
            ShowRestored?.Invoke(RestoreFailedTitle, ex is NotABackupException ? GameBackups.NotOursText : "Windows couldn't open that file, so nothing was restored.", true);
            return;
        }
        if (Confirm?.Invoke(RestoreTitle,
                $"Restore the backup made on {GameBackups.When(info)}? Every character in it gets the settings and macros from it back, over the " +
                "ones they have now. Characters that aren't in it keep theirs. Provenance backs up your current settings first, so you can go back " +
                "to them by restoring that backup.", "RESTORE", false) != true) return;
        RestoringBackup = true;
        SayBackup("Restoring your game settings...");
        try
        {
            var (done, text) = await Task.Run(() => GameBackups.Restore(zip, info, sources, folder, RestoreBlocked));
            SayBackup("");
            ShowRestored?.Invoke(done ? RestoreTitle : RestoreFailedTitle, text, !done);
        }
        finally { RestoringBackup = false; }
    }

    private void SayBackup(string text, bool error = false)
    {
        BackupStatus = text;
        BackupStatusIsError = error;
    }
}
