using System.ComponentModel;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels.Social;

// what the chat view models need of the friends side: the session, what blocks a write and the viewer's text
internal interface ISocialSession : INotifyPropertyChanged
{
    int? SessionPid { get; }
    string AccountName { get; }
    bool GameRunning { get; }
    bool HandleSwitching { get; }
    string? WriteBlockedNote { get; }
    TimeProvider Time { get; }
    PolSocialSnapshot Last { get; }
    string? LeavingGroupId { get; }
    string Filtered(string text);
    string ViewerText(int id, string fallback);
    event Action<PolSocialSnapshot>? SnapshotApplied;
}
