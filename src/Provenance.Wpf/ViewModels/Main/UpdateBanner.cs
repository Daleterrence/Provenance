using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.ViewModels.Main;

public sealed partial class UpdateBanner : ObservableObject
{
    private readonly UpdateRestart _restart;
    private bool _showing;

    public UpdateBanner(UpdateRestart restart)
    {
        _restart = restart;
        restart.BusyChanged += Refresh;
    }

    public bool Showing => _showing;

    public string Text => SelfUpdate.WaitingText;

    public string? RestartTip => _restart.BlockedReason;

    internal void Show() => SetProperty(ref _showing, true, nameof(Showing));

    internal void Refresh()
    {
        OnPropertyChanged(nameof(RestartTip));
        RestartNowCommand.NotifyCanExecuteChanged();
    }

    private bool CanRestart() => !_restart.Busy && _restart.BlockedReason is null;

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartNowAsync() => _restart.AskAsync();
}
