using System.ComponentModel;
using System.Windows.Media;
using Provenance.Common.PolFormat;
using Provenance.Common.Social;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.ViewModels.Social;

// what the chat view models need of the friends side: the session, what blocks a write and the viewer's text
internal interface ISocialSession : INotifyPropertyChanged
{
    SessionState Session { get; }
    string AccountName { get; }
    ImageSource? Avatar { get; }
    bool ProfilePictureSaving { get; }
    bool GameRunning { get; }
    bool HandleSwitching { get; }
    string? WriteBlockedNote { get; }
    DateTime Now { get; }
    PolSocialSnapshot Last { get; }
    string? LeavingGroupId { get; }
    ViewerResources Viewer { get; }
    event Action<PolSocialSnapshot>? SnapshotApplied;
}
