using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Game.ThirdParty;
using Provenance.Wpf.Services;
using Serilog;

namespace Provenance.Wpf.ViewModels.Settings;

public sealed partial class OpenFolder : ObservableObject
{
    private readonly Func<string?> _folder;

    internal OpenFolder(Func<string?> folder) => _folder = folder;

    private string? There() =>
        _folder() is { Length: > 0 } f && Path.IsPathFullyQualified(f) && ThirdPartyHooks.Usable(f) is { } usable && Directory.Exists(usable) ? usable : null;

    public bool Exists => There() is not null;

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
