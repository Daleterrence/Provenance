using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Settings;

namespace Provenance.Wpf.ViewModels.Settings;

// one row for each kind of notice, with a switch for the notice and one for the taskbar flash. Every change saves
// straight away
public sealed partial class NoticeRow : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly NoticeKind _kind;

    internal NoticeRow(ISettingsService settings, NoticeKind kind, string label, string? help)
    {
        _settings = settings;
        _kind = kind;
        Label = label;
        Help = help;
        _shows = settings.Current.Shows(kind);
        _flashes = settings.Current.Flashes(kind);
    }

    public string Label { get; }
    public string? Help { get; }
    public bool HasFlash => _kind != NoticeKind.FriendOnline;
    public string ShowsName => $"{Label}, show a notice";
    public string FlashesName => $"{Label}, flash the taskbar";

    [ObservableProperty] private bool _shows;
    [ObservableProperty] private bool _flashes;

    partial void OnShowsChanged(bool value) => _settings.SaveChange(s => s.SetShows(_kind, value));
    partial void OnFlashesChanged(bool value) => _settings.SaveChange(s => s.SetFlashes(_kind, value));
}

public sealed class NotificationSettings
{
    public const string IntroText = "Choose which notices Provenance shows, and which ones flash its taskbar button.";

    internal NotificationSettings(ISettingsService settings)
    {
        Rows =
        [
            new(settings, NoticeKind.FriendOnline, "A friend comes online", null),
            new(settings, NoticeKind.Message, "A new message", null),
            new(settings, NoticeKind.FriendRequest, "A friend request", null),
            new(settings, NoticeKind.GroupInvite, "A group invitation", null),
            new(settings, NoticeKind.Answer, "Answers and group changes",
                "When someone accepts or declines your friend request or group invitation, or a friend group you're in is disbanded or you're removed from it."),
            new(settings, NoticeKind.GroupChat, "New group chat lines", "Only while the group chat window isn't in front."),
        ];
    }

    public IReadOnlyList<NoticeRow> Rows { get; }
}
