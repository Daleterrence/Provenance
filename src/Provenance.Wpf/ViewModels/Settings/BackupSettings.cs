using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Game;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Patcher;
using Provenance.Common.Settings;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Shared;
using Serilog;

namespace Provenance.Wpf.ViewModels.Settings;

public sealed record BackupRow(BackupInfo Info)
{
    public string When => GameBackupPolicy.When(Info);
    public string Characters => Info.Characters == 1 ? "1 character" : $"{Info.Characters} characters";
    public string RestoreName => $"Restore the backup made on {When}";
}

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

    public const string GoneText = "That backup isn't in the folder any more, so nothing was restored.";

    public const string NoBackupsText = "There aren't any backups in this folder yet.";

    // newest first, the ones in the folder as it was last read
    public ObservableCollection<BackupRow> Items { get; } = [];
    [ObservableProperty] private bool _listed;
    public bool Empty => Listed && Items.Count == 0;
    // why RESTORE is off, when it is
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRestoreNow))] private string? _restoreNote;
    public bool CanRestoreNow => RestoreNote is null && !RestoringBackup && !BackingUp;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RestoreFromCommand), nameof(RestoreFromFileCommand), nameof(BackUpNowCommand)),
     NotifyPropertyChangedFor(nameof(CanRestoreNow), nameof(CanBackUpNow))]
    private bool _restoringBackup;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RestoreFromCommand), nameof(RestoreFromFileCommand), nameof(BackUpNowCommand)),
     NotifyPropertyChangedFor(nameof(CanRestoreNow), nameof(CanBackUpNow))]
    private bool _backingUp;

    public const string BackUpBlockedText = "You can back up once Provenance has finished starting your accounts.";

    // why BACK UP NOW is off, when it's the accounts starting. A backup doesn't mind the game or a login, the automatic
    // ones run in the middle of both
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanBackUpNow))] private string? _backUpNote;

    // an empty box is the default folder, and a backup makes the folder if it isn't there yet
    public bool CanBackUpNow => BackUpNote is null && !RestoringBackup && !BackingUp;

    // what BACK UP NOW and RESTORE are doing or did, under the list's buttons
    public StatusLine ActionStatus { get; } = new();

    internal Action<string> ShowBackupFolderForTest { get; set; } = Explorer.ShowFolder;
    internal Func<LauncherSettings, BackupSources?>? RestoreSourcesForTest { get; set; }

    private const string RestoreTitle = "Restore game settings", RestoreFailedTitle = "Couldn't restore the backup", BackUpFailedTitle = "Couldn't back up your game settings";

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
        _ = Reload();
    }

    internal Task Listing { get; private set; } = Task.CompletedTask;
    private int _reads;

    // the folder's read again when the tab comes up, the window comes back to the front and the folder changes. A
    // newer read drops an older one
    public Task Reload()
    {
        RefreshRestoreNote();
        return Listing = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        int mine = ++_reads;
        var folder = GameSettingsBackup.FolderOrDefault(_settings.Current.BackupFolder);
        IReadOnlyList<BackupInfo> found;
        try { found = await Task.Run(() => ListBackups(folder)); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not list the backups in {Folder}", folder);
            found = [];
        }
        if (mine != _reads) return;
        // left alone when nothing's changed, so a focused RESTORE keeps its focus and nothing's announced again
        static (string, DateTime, int, int) Key(BackupInfo i) => (i.Path, i.CreatedUtc, i.Characters, i.Files);
        if (!Items.Select(r => Key(r.Info)).SequenceEqual(found.Select(Key)))
        {
            Items.Clear();
            foreach (var info in found) Items.Add(new BackupRow(info));
        }
        Listed = true;
        OnPropertyChanged(nameof(Empty));
    }

    internal Func<string, IReadOnlyList<BackupInfo>> ListBackups { get; set; } = GameSettingsBackup.List;

    // the same reasons a restore gives, said above the list so the buttons going grey isn't a mystery
    public void RefreshRestoreNote()
    {
        RestoreNote = RestoreBlocked();
        BackUpNote = _context.MultiRunning() ? BackUpBlockedText : null;
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
            // a share that doesn't answer isn't waited on
            if (ThirdPartyHooks.Usable(folder) is null) throw new IOException("The share doesn't answer.");
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

    private bool CanRestore() => !RestoringBackup && !BackingUp;

    internal Task BackingUpNow { get; private set; } = Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanBackUp))]
    private Task BackUpNow() => BackingUpNow = BackUpNowAsync();

    private bool CanBackUp() => !RestoringBackup && !BackingUp;

    private async Task BackUpNowAsync()
    {
        if (_context.MultiRunning())
        {
            _dialogs.Error(BackUpFailedTitle, BackUpBlockedText);
            return;
        }
        var s = _settings.Current;
        var sources = RestoreSourcesForTest is { } test ? test(s) : _settings.DetectedRegion is { } r ? GameBackupPolicy.SourcesFor(s, r, null) : null;
        if (sources is null)
        {
            _dialogs.Error(BackUpFailedTitle, "FINAL FANTASY XI isn't installed on this PC, so there's nothing to back up.");
            return;
        }
        var folder = GameSettingsBackup.FolderOrDefault(s.BackupFolder);
        BackingUp = true;
        ActionStatus.Show(GameBackupPolicy.WorkingText);
        try
        {
            var (done, text) = await Task.Run(() => GameBackupPolicy.BackUpNow(sources, folder));
            if (done) ActionStatus.Show(text, TimeSpan.FromSeconds(8));
            else
            {
                ActionStatus.Clear();
                _dialogs.Error(BackUpFailedTitle, text);
            }
        }
        finally
        {
            BackingUp = false;
            await Reload();
        }
    }

    internal Task Restoring { get; private set; } = Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreFrom(BackupRow? row) => Restoring = row is null ? Task.CompletedTask : RestoreBackupAsync(row.Info.Path, null);

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreFromFile() => Restoring = RestoreBackupAsync(null, null);

    private async Task RestoreBackupAsync(string? zip, BackupInfo? info)
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
        if (zip is null)
        {
            if (_dialogs.PickFile("Choose a backup to restore", $"Game settings backups|{GameSettingsBackup.NameStart}*.zip", folder) is not { } picked) return;
            zip = picked;
        }
        if (info is null)
        {
            try { info = GameSettingsBackup.Check(zip); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException || !File.Exists(zip))
            {
                Log.Warning(ex, "Restore: {Zip} has gone", zip);
                _dialogs.Error(RestoreFailedTitle, GoneText);
                await Reload();
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Restore: {Zip} isn't a backup that can be restored", zip);
                _dialogs.Error(RestoreFailedTitle, ex is NotABackupException ? GameBackupPolicy.NotOursText : "Windows couldn't open that file, so nothing was restored.");
                return;
            }
        }
        if (!_dialogs.Confirm(RestoreTitle,
                $"Restore the backup made on {GameBackupPolicy.When(info)}? Every character in it gets the settings and macros from it back, over the " +
                "ones they have now. Characters that aren't in it keep theirs. Provenance backs up your current settings first, so you can go back " +
                "to them by restoring that backup.", "RESTORE")) return;
        RestoringBackup = true;
        ActionStatus.Show("Restoring your game settings...");
        try
        {
            var (done, text) = await Task.Run(() => GameBackupPolicy.Restore(zip, info, sources, folder, RestoreBlocked));
            ActionStatus.Clear();
            if (done) _dialogs.Info(RestoreTitle, text);
            else _dialogs.Error(RestoreFailedTitle, text);
        }
        finally
        {
            RestoringBackup = false;
            await Reload();
        }
    }
}
