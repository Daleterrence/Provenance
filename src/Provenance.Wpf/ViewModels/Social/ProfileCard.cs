using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels.Social;

public sealed partial class ProfileCard : ObservableObject
{
    public ProfileCard(MyProfileCharacter? character) => Character = character;

    public MyProfileCharacter? Character { get; }
    public bool IsHandle => Character is null;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private string _title = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private string _subtitle = "";
    [ObservableProperty] private ImageSource? _picture;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _hasChanges;

    public string Summary => Subtitle.Length > 0 ? $"{Title}, {Subtitle}" : Title;
}
