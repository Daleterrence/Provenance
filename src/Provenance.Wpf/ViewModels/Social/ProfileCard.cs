using System.Collections.ObjectModel;
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

    // a character's profile rows as the friend pages lay them out, and whether its last read failed
    public ObservableCollection<FriendProfileViewModel.RowItem> Rows { get; } = new();
    public bool ReadFailed { get; set; }

    public string Summary => Subtitle.Length > 0 ? $"{Title}, {Subtitle}" : Title;
}
