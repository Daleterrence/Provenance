using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Game.ThirdParty;
using Provenance.Wpf.Services;
using Serilog;

namespace Provenance.Wpf.ViewModels.Settings;

// the Open folder button next to a path. It only opens Explorer, so it's off while the folder isn't there and never
// makes one
public sealed partial class OpenFolder : ObservableObject
{
    private readonly Func<string?> _folder;

    internal OpenFolder(Func<string?> folder) => _folder = folder;

    // the folder has to be a whole path (a relative one would be the elevated process's), and one on a share that
    // doesn't answer is as good as gone, the check's the one the Windower and Ashita boxes get
    private string? There() =>
        _folder() is { Length: > 0 } f && Path.IsPathFullyQualified(f) && ThirdPartyHooks.Usable(f) is { } usable && Directory.Exists(usable) ? usable : null;

    public bool Exists => There() is not null;

    // read again when a page comes up or the window comes back to the front, the folder may have turned up
    public void Refresh()
    {
        OnPropertyChanged(nameof(Exists));
        OpenCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(Exists))]
    private void Open()
    {
        if (There() is not { } f) return;
        try { Explorer.ShowFolder(f); }
        catch (Exception ex) { Log.Warning(ex, "Could not open {Folder}", f); }
    }
}
