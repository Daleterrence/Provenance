using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Game;
using Provenance.Common.Patcher;
using Provenance.Common.Settings;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Shared;
using Serilog;

namespace Provenance.Wpf.ViewModels.Settings;

public partial class BackupSettings : ObservableObject
{
    private readonly IDialogs _dialogs;
    private readonly ISettingsService _settings;
    private readonly SettingsContext _context;

    internal BackupSettings(IDialogs dialogs, ISettingsService settings, SettingsContext context)
    {
        _dialogs = dialogs;
        _settings = settings;
        _context = context;
        _backupGameSettings = settings.Current.BackupGameSettings;
        _backupFolder = settings.Current.BackupFolder;
    }

    [ObservableProperty] private bool _backupGameSettings;
    [ObservableProperty] private string _backupFolder = "";
    public StatusLine BackupStatus { get; } = new();

    public string DefaultBackupFolder => GameSettingsBackup.DefaultFolder;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RestoreBackupCommand))] private bool _restoringBackup;

    internal Action<string> ShowBackupFolderForTest { get; set; } = Explorer.ShowFolder;
    internal Func<LauncherSettings, BackupSources?>? RestoreSourcesForTest { get; set; }

    private const string RestoreTitle = "Restore game settings", RestoreFailedTitle = "Couldn't restore the backup";

    partial void OnBackupGameSettingsChanged(bool value) => _settings.SaveChange(s => s.BackupGameSettings = value);

    // an empty box is the default folder
    partial void OnBackupFolderChanged(string value)
    {
        var folder = value.Trim();
        if (folder.Length > 0 && !Path.IsPathFullyQualified(folder))
        {
            BackupStatus.Fail(@"Type the whole path of a folder, like D:\FFXI backups.");
            return;
        }
        BackupStatus.Clear();
        _settings.SaveChange(s => s.BackupFolder = folder);
    }

    [RelayCommand]
    private void BrowseBackupFolder()
    {
        if (_dialogs.PickFolder("Choose a folder for your game settings backups") is { } d) BackupFolder = d;
    }

    // the folder there's no backup in yet gets made, so there's something to open
    [RelayCommand]
    private void OpenBackupFolder()
    {
        var folder = GameSettingsBackup.FolderOrDefault(_settings.Current.BackupFolder);
        try
        {
            if (!Directory.Exists(folder))
                using (GameSettingsBackup.HoldFolder(folder) ?? throw new BackupFolderLinkedException(folder)) { }
            ShowBackupFolderForTest(folder);
        }
        catch (BackupFolderLinkedException)
        {
            BackupStatus.Fail("Your backups folder goes through a link to somewhere else, so Provenance won't make it. Choose another folder.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open the backups folder {Folder}", folder);
            BackupStatus.Fail("Windows couldn't open that folder.");
        }
    }

    // the game writes its settings when a character logs out, so a restore under a running one wouldn't last. Asked
    // again right before anything's written
    internal string? RestoreBlocked() =>
        _context.GameRunning() ? "FINAL FANTASY XI is running, and it would write over the restored settings when you log out. Close the game, then restore the backup."
        : _context.LoggedIn() ? "You can restore a backup once you're logged out of PlayOnline."
        : _context.MultiRunning() ? "You can restore a backup once Provenance has finished starting your accounts."
        : null;

    private bool CanRestore() => !RestoringBackup;

    internal Task Restoring { get; private set; } = Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreBackup() => Restoring = RestoreBackupAsync();

    private async Task RestoreBackupAsync()
    {
        if (RestoreBlocked() is { } blocked)
        {
            _dialogs.Error(RestoreFailedTitle, blocked);
            return;
        }
        var s = _settings.Current;
        var sources = RestoreSourcesForTest is { } test ? test(s) : _settings.DetectedRegion is { } r ? GameBackupPolicy.SourcesFor(s, r, null) : null;
        if (sources is null)
        {
            _dialogs.Error(RestoreFailedTitle, "FINAL FANTASY XI isn't installed on this PC, so there's nowhere to restore the backup to.");
            return;
        }
        var folder = GameSettingsBackup.FolderOrDefault(s.BackupFolder);
        if (_dialogs.PickFile("Choose a backup to restore", $"Game settings backups|{GameSettingsBackup.NameStart}*.zip", folder) is not { } zip) return;
        BackupInfo info;
        try { info = GameSettingsBackup.Check(zip); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Restore: {Zip} isn't a backup that can be restored", zip);
            _dialogs.Error(RestoreFailedTitle, ex is NotABackupException ? GameBackupPolicy.NotOursText : "Windows couldn't open that file, so nothing was restored.");
            return;
        }
        if (!_dialogs.Confirm(RestoreTitle,
                $"Restore the backup made on {GameBackupPolicy.When(info)}? Every character in it gets the settings and macros from it back, over the " +
                "ones they have now. Characters that aren't in it keep theirs. Provenance backs up your current settings first, so you can go back " +
                "to them by restoring that backup.", "RESTORE")) return;
        RestoringBackup = true;
        BackupStatus.Show("Restoring your game settings...");
        try
        {
            var (done, text) = await Task.Run(() => GameBackupPolicy.Restore(zip, info, sources, folder, RestoreBlocked));
            BackupStatus.Clear();
            if (done) _dialogs.Info(RestoreTitle, text);
            else _dialogs.Error(RestoreFailedTitle, text);
        }
        finally { RestoringBackup = false; }
    }
}
