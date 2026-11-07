using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Provenance.Wpf.ViewModels.Social;

// Handle Profile (Slot -1) or one of the handle's FFXI characters
public sealed partial class ProfileTab(int slot, string characterKey, string label) : ObservableObject
{
    public int Slot { get; } = slot;
    public string CharacterKey { get; } = characterKey;
    public bool IsHandle => Slot < 0;
    public ImageSource? Picture { get; init; }
    [ObservableProperty] private string _label = label;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Clickable))] private bool _isSelected;
    // a tab whose read failed can be clicked again to retry
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Clickable))] private bool _failed;
    public bool Clickable => !IsSelected || Failed;
}
