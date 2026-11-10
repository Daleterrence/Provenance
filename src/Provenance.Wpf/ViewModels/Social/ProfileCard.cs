using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels.Social;

public sealed partial class ProfileCard : ObservableObject
{
    private readonly Func<PrivacyLevel, string> _help;

    public ProfileCard(MyProfileCharacter? character, Func<PrivacyLevel, string> help)
    {
        Character = character;
        _help = help;
    }

    public MyProfileCharacter? Character { get; }
    public bool IsHandle => Character is null;
    public string Key => Character?.Key ?? "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private string _title = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private string _subtitle = "";
    [ObservableProperty] private ImageSource? _picture;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _hasChanges;
    [ObservableProperty] private bool _loaded;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VisibilityHelp))] private PrivacyLevel _level;

    public string Summary => Subtitle.Length > 0 ? $"{Title}, {Subtitle}" : Title;
    public string VisibilityHelp => _help(Level);
}
