using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Hook;
using Provenance.Common.Settings;

namespace Provenance.Wpf.ViewModels.Settings;

public sealed partial class NoticeRow : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly NoticeKind _kind;

    internal NoticeRow(ISettingsService settings, NoticeKind kind, string label, string help)
    {
        _settings = settings;
        _kind = kind;
        Label = label;
        Help = help;
        _shows = settings.Current.Shows(kind);
        _flashes = settings.Current.Flashes(kind);
    }

    public string Label { get; }
    public string Help { get; }
    public bool HasFlash => _kind != NoticeKind.FriendOnline;
    public string ShowsName => $"{Label}, show a notice";
    public string FlashesName => $"{Label}, flash the taskbar";

    [ObservableProperty] private bool _shows;
    [ObservableProperty] private bool _flashes;

    partial void OnShowsChanged(bool value) => _settings.SaveChange(s => s.SetShows(_kind, value));
    partial void OnFlashesChanged(bool value) => _settings.SaveChange(s => s.SetFlashes(_kind, value));
}

public sealed partial class NotificationSettings : ObservableObject
{
    public const string IntroText = "Choose which notices Provenance shows, and which ones flash its taskbar button.";
    private readonly ISettingsService _settings;

    internal NotificationSettings(ISettingsService settings)
    {
        _settings = settings;
        _gameSound = settings.Current.GameMessageSound;
        _gameFriendLines = settings.Current.GameFriendLines;
        _gamePlayingLines = settings.Current.GamePlayingLines;
        Rows =
        [
            new(settings, NoticeKind.FriendOnline, "A friend comes online", "A quiet notice. Not shown while you're in the game."),
            new(settings, NoticeKind.Message, "A new message", "When a friend sends you a message."),
            new(settings, NoticeKind.FriendRequest, "A friend request", "When someone sends you a friend request."),
            new(settings, NoticeKind.GroupInvite, "A group invitation", "When someone invites you to a friend group."),
            new(settings, NoticeKind.Answer, "Answers and group changes", "Replies to your requests and invitations, and group changes."),
            new(settings, NoticeKind.GroupChat, "New group chat lines", "Only while the group chat window isn't in front."),
        ];
    }

    public IReadOnlyList<NoticeRow> Rows { get; }

    [ObservableProperty] private bool _gameSound;
    [ObservableProperty] private bool _gameFriendLines;
    [ObservableProperty] private bool _gamePlayingLines;

    partial void OnGameSoundChanged(bool value)
    {
        _settings.SaveChange(s => s.GameMessageSound = value);
        HookFiles.SetGameSound(value);
    }

    partial void OnGameFriendLinesChanged(bool value)
    {
        _settings.SaveChange(s => s.GameFriendLines = value);
        HookFiles.SetGameFriendLines(value);
    }

    partial void OnGamePlayingLinesChanged(bool value)
    {
        _settings.SaveChange(s => s.GamePlayingLines = value);
        HookFiles.SetGamePlayingLines(value);
    }
}
