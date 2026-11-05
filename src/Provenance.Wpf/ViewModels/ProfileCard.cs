using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels;

// a card along the top of the editor: my handle's profile, or one of my FFXI characters (read only)
public sealed partial class ProfileCard : ObservableObject
{
    private readonly Func<long, string> _help;

    public ProfileCard(MyProfileCharacter? character, Func<long, string> help)
    {
        Character = character;
        _help = help;
    }

    public MyProfileCharacter? Character { get; }
    public bool IsHandle => Character is null;
    public string Key => Character?.Key ?? "";

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private ImageSource? _picture;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _hasChanges;
    [ObservableProperty] private bool _loaded;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VisibilityHelp))] private long _level;

    public string VisibilityHelp => _help(Level);
}
