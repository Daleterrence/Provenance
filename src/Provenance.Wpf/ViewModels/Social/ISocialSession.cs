using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using Provenance.Common.PolFormat;
using Provenance.Common.Social;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.ViewModels.Social;

internal interface ISocialSession : INotifyPropertyChanged
{
    SessionState Session { get; }
    string AccountName { get; }
    ImageSource? Avatar { get; }
    bool ProfilePictureSaving { get; }
    ICommand ChangeProfilePictureCommand { get; }
    bool GameRunning { get; }
    bool HandleSwitching { get; }
    string? WriteBlockedNote { get; }
    DateTime Now { get; }
    PolSocialSnapshot Last { get; }
    string? LeavingGroupId { get; }
    IDictionary<string, PolFfxiProfile> OwnCharacterProfiles { get; }
    ViewerResources Viewer { get; }
    event Action<PolSocialSnapshot>? SnapshotApplied;
}
