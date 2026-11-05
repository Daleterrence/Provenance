using CommunityToolkit.Mvvm.ComponentModel;

namespace Provenance.Wpf.ViewModels.Settings;

public sealed partial class MultiAccountRow : ObservableObject
{
    public required string ProfileId { get; init; }
    public required string Name { get; init; }
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string _thirdPartyHookProfile = "";
    [ObservableProperty] private bool _credentialsRefused;
    [ObservableProperty] private string? _profileNeeded;
    internal Action? Changed;

    partial void OnEnabledChanged(bool value)
    {
        if (value) Problem = null;
        Changed?.Invoke();
    }

    partial void OnThirdPartyHookProfileChanged(string value) => Changed?.Invoke();
}
