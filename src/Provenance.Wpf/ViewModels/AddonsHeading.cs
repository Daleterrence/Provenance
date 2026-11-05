using CommunityToolkit.Mvvm.ComponentModel;

namespace Provenance.Wpf.ViewModels;

public sealed partial class AddonsHeading(string? help = null) : ObservableObject
{
    public string? Help { get; } = help;
    [ObservableProperty] private string? _note;
    [ObservableProperty] private string? _warning;
}
