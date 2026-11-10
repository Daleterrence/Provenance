using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Hook;
using Provenance.Common.Social;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Social;

public sealed partial class SocialSearch : ObservableObject
{
    private readonly SocialViewModel _social;

    public SocialSearch(SocialViewModel social) => _social = social;

    // Name is the character's for a character hit, which shows like a friend in game does
    public sealed record SearchHitItem(int Index, string Name, ImageSource? Avatar, bool IsFriend, string Key,
                                       bool RequestSent = false, bool IsMe = false)
    {
        public bool IsCharacter { get; init; }
        public string World { get; init; } = "";
        public string Label => IsCharacter && World.Length > 0 ? PlayingText.For(Name, World).Long : Name;
        public string Note =>
            IsMe ? "This is you" : IsFriend ? "Already on your friend list" : RequestSent ? "Friend request sent"
            : IsCharacter && World.Length == 0 ? "World not shown" : "";
    }

    public ObservableCollection<SearchHitItem> Hits { get; } = new();
    [ObservableProperty] private bool _busy;
    public StatusLine Note { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ByHandle), nameof(ByHid), nameof(ByCharacter), nameof(Hint), nameof(Intro), nameof(MaxLength))]
    private FriendSearchKind _kind;
    // the picker's off while a search runs, so the hits always match the kind showing
    partial void OnKindChanged(FriendSearchKind value)
    {
        if (Busy) return;
        Hits.Clear();
        Note.Clear();
    }
    public bool ByHandle { get => Kind == FriendSearchKind.Handle; set { if (value) Kind = FriendSearchKind.Handle; } }
    public bool ByHid { get => Kind == FriendSearchKind.Hid; set { if (value) Kind = FriendSearchKind.Hid; } }
    public bool ByCharacter { get => Kind == FriendSearchKind.Character; set { if (value) Kind = FriendSearchKind.Character; } }
    public string Hint => Kind switch
    {
        FriendSearchKind.Hid => "H-ID, like 00-1234567",
        FriendSearchKind.Character => "Character name",
        _ => "Handle name",
    };
    public string Intro => Kind switch
    {
        FriendSearchKind.Hid => "Search PlayOnline for a handle by its H-ID. Every handle has its own, shown in its profile.",
        FriendSearchKind.Character => "Search for a FINAL FANTASY XI character by name. The same name can be on more than one world, so check the world (a private profile doesn't show it).",
        _ => "Search PlayOnline for a handle name. Many people can share one name, so check the picture.",
    };
    public int MaxLength => Kind == FriendSearchKind.Hid ? PolLimits.HidSearch : PolLimits.HandleName;

    private string? _searchToken;
    private DateTime _searchAt;
    private FriendSearchKind _searchedKind;
    private string _searchedValue = "";
    private string? _requestToken;
    private string _requestName = "";
    private string _requestKey = "";
    private bool _requestCharacter;
    private DateTime _requestAt;
    private const int SearchPage = 100;   // the hook fetches the first 100, like the viewer
    private const string TooManyResults = "Too many results. Please narrow your search.";

    internal string? _answerToken;
    private FriendItem? _answerFor;
    private RequestAnswer _answerKind;
    private DateTime _answerAt;

    internal bool Writing => _answerToken != null || _requestToken != null;

    internal void Reset()
    {
        _answerToken = _searchToken = _requestToken = null;
        _answerFor = null;
        Busy = false;
        Note.Clear();
        Hits.Clear();
    }

    public void Run(string text)
    {
        var value = text.Trim();
        var kind = Kind;
        if (Busy || !_social.LoggedIn || _social.SessionPid is not { } pid) return;
        if (_social.GameRunning)
        {
            Note.Fail(SocialViewModel.GameRunningNote);
            return;
        }
        if (!FriendSearch.Ok(kind, value))
        {
            Note.Fail(kind switch
            {
                FriendSearchKind.Hid => HookFailures.Friends.BadHid,
                FriendSearchKind.Character => HookFailures.Friends.BadCharacterName,
                _ => HookFailures.Friends.BadHandleName,
            });
            return;
        }
        if (kind == FriendSearchKind.Handle && value.Contains('\''))
        {
            Note.Fail(HookFailures.Friends.QuoteInName);
            return;
        }
        _searchToken = HookFiles.NewToken();
        _searchAt = _social.Now;
        _searchedKind = kind;
        _searchedValue = value;
        Hits.Clear();
        Busy = true;
        Note.Show("Searching PlayOnline...");
        if (SocialRequests.Search(pid, _searchToken, kind, value)) return;
        _searchToken = null;
        Busy = false;
        Note.Fail(HookFailures.RequestWriteFailed);
    }

    // a search nobody's waiting on any more: withdrawn if the hook hasn't taken it yet, and its answer's ignored
    public void Cancel()
    {
        if (_searchToken is null) return;
        _social.DiscardRequest(HookContract.Requests.Search);
        _searchToken = null;
        Busy = false;
        Note.Clear();
        Hits.Clear();
    }

    public bool CanRequest(SearchHitItem? hit) => HitProblem(hit) is null;

    public string? RequestProblem(SearchHitItem? hit) => HitProblem(hit) ?? RequestBlocked;

    private string? HitProblem(SearchHitItem? hit) => hit switch
    {
        null => Kind == FriendSearchKind.Character ? "Choose a character from the list first." : "Choose a handle from the list first.",
        { IsMe: true } => "That's one of your own handles. You can't add yourself as a friend.",
        { IsFriend: true } => HookFailures.Friends.AlreadyFriend(hit.Name),
        { RequestSent: true } => $"You've already sent {hit.Name} a friend request.",
        _ => null,
    };

    internal string? RequestBlocked =>
        _social.CanEdit ? null
        : _social.GameRunning ? SocialViewModel.GameRunningNote
        : _social.EditBusy ? "Wait for your last change to finish first."
        : "Log in to send friend requests.";

    public void AskSendRequest(SearchHitItem hit)
    {
        if (_social.Dialogs.Confirm("Send friend request",
                $"Send \"Let's be friends!\" to {hit.Name}? They'll show on your list" +
                (hit.IsCharacter ? " under their PlayOnline handle," : "") + " as \"Friend request sent\" until they accept.",
                "SEND"))
            SendRequest(hit);
    }

    public void SendRequest(SearchHitItem hit)
    {
        if (hit.IsMe)
        {
            Note.Fail(HookFailures.Friends.RequestSelf);
            return;
        }
        if (hit.IsFriend || hit.RequestSent || _requestToken != null || !_social.BeginEdit(out var pid, out var token, followSend: true)) return;
        _requestToken = token;
        _requestName = hit.Name;
        _requestKey = hit.Key;
        _requestCharacter = hit.IsCharacter;
        _requestAt = _social.Now;
        Note.Show($"Sending a friend request to {hit.Name}...");
        if (SocialRequests.SendFriendRequest(pid, token, hit.Index, hit.Key, _social.ViewerText(0x46c9, "Let's be friends!"),
                                             _social.ViewerText(0x46e6, "Would you like to be friends?"))) return;
        _requestToken = null;
        _social.EditBusy = false;
        Note.Fail(HookFailures.RequestWriteFailed);
    }

    // Accept and Decline reply with the viewer's fixed subject, Ignore ignores them and declines
    public void AnswerRequest(FriendItem f, RequestAnswer answer)
    {
        if (!f.IsRequest) return;
        if (f.IsMe && answer != RequestAnswer.Decline)
        {
            _social.EditNote.Fail(HookFailures.Friends.OwnRequest);
            return;
        }
        if (answer == RequestAnswer.Accept && _social.Friends.Count(x => x.IsFriend) >= PolLimits.MaxFriends)
        {
            _social.EditNote.Fail(HookFailures.FriendListFull);
            return;
        }
        if (answer == RequestAnswer.Ignore && f.Key.Length > 0 && _social.Blocked.Any(x => x.Key == f.Key))
        {
            _social.EditNote.Fail($"{f.Name} is already on your ignore list.");
            return;
        }
        if (!_social.BeginEdit(out var pid, out var token, followSend: true)) return;
        _answerToken = token;
        _answerFor = f;
        _answerKind = answer;
        _answerAt = _social.Now;
        var subject = answer == RequestAnswer.Accept
            ? _social.ViewerText(0x46d0, "Friend registration accepted")
            : _social.ViewerText(0x46d1, "Friend registration declined");
        // ignoring from a request only ever covers this handle, same as the viewer
        if (SocialRequests.AnswerRequest(pid, answer, token, f.RequestMid, subject, allHandles: false)) return;
        _answerToken = null;
        _answerFor = null;
        _social.WriteOrFail(false);
    }

    internal void Apply(PolSocialSnapshot snap)
    {
        ApplyAnswerState(snap);
        ApplySearchState(snap);
    }

    private void ApplyAnswerState(PolSocialSnapshot snap)
    {
        if (_answerToken is null || _answerFor is not { } f) return;
        // a list upload and a send, each up to 100 secs
        if (_social.Settled(snap.SendStateFor(_answerToken), "sent", _answerAt, 250) is not { } st) return;
        _answerToken = null;
        _answerFor = null;
        _social.EditBusy = false;
        if (st == "sent")
        {
            _social.Answered(f.RequestMid, ack: true);
            _social.Chat?.AddNote(f.Name, f.Key, _answerKind switch
            {
                RequestAnswer.Accept => "You accepted the friend request.",
                RequestAnswer.Decline => "You declined the friend request.",
                _ => "You declined the friend request and ignored this person.",
            });
            return;
        }
        // it may still have gone out, so it leaves the list for this session rather than invite a second answer
        if (st == "timeout") _social.Answered(f.RequestMid, ack: false);
        _social.EditNote.Fail(st switch
        {
            "timeout" => HookFailures.Friends.AnswerNoReply,
            "failed:handle changing" => HookFailures.Friends.AnswerHandleChanging,
            "failed:busy" => HookFailures.Busy,
            "failed:no handle" => HookFailures.Friends.AnswerHandleGone,
            "failed:no such request" => HookFailures.Friends.NoSuchRequest,
            "failed:self" => HookFailures.Friends.OwnRequest,
            "failed:already ignored" => HookFailures.IgnoredAlready,
            "failed:-7188" => HookFailures.FriendListFull,
            "failed:no such friend" => HookFailures.Friends.AnswerFriendGone,
            "failed:stopped" => HookFailures.Friends.AnswerStopped,
            "failed:hourly limit" => HookFailures.Friends.AnswerHourlyLimit,
            var s when s.StartsWith("failed:list", StringComparison.Ordinal) =>
                HookFailures.Friends.AnswerListNotSaved,
            var s => HookFailures.Friends.AnswerRejected(SocialRequests.FailReason(s)),
        });
    }

    private void ApplySearchState(PolSocialSnapshot snap)
    {
        // two profile-server jobs, each up to polcore's 100 secs
        _social.WaitingTurn(ref _searchAt, _searchToken, snap.SearchJob.Token, snap);
        if (_searchToken != null &&
            _social.Settled(snap.SearchJob.Token == _searchToken ? snap.SearchJob.State : null, "done", _searchAt, 250) is { } st)
        {
            if (st == "timeout") _social.DiscardRequest(HookContract.Requests.Search);
            _searchToken = null;
            Busy = false;
            var characters = _searchedKind == FriendSearchKind.Character;
            if (st == "done" && snap.SearchTooMany) Note.Fail(TooManyResults);
            else if (st == "done")
            {
                Hits.Clear();
                foreach (var h in snap.SearchResults)
                {
                    // the search matched INITCAP of what was typed, so a hit with no name in it is still called that
                    var name = characters && h.Name.Length == 0 ? FriendSearch.InitCap(_searchedValue) : h.Name;
                    Hits.Add(new SearchHitItem(h.Index, name, _social.Avatars.ProfilePicture(characters ? 0 : h.ProfilePictureId),
                                               h.IsFriend, h.Key, IsMe: h.IsMe)
                             { IsCharacter = characters, World = characters ? h.World : "" });
                }
                var what = characters ? "character" : "handle";
                // only past the first page were there more than came back, the hook drops a hit with nothing in it
                var total = snap.SearchTotal > SearchPage ? snap.SearchTotal : Hits.Count;
                Note.Show(Hits.Count == 0 ? _searchedKind switch
                          {
                              FriendSearchKind.Hid => HookFailures.Friends.NoHidHit,
                              FriendSearchKind.Character => HookFailures.Friends.NoCharacterHit,
                              _ => HookFailures.Friends.NoHandleHit,
                          }
                        : total > Hits.Count ? $"Found {total} {what}s. Showing {Hits.Count} of them."
                        : Hits.Count == 1 ? $"Found 1 {what}." : $"Found {Hits.Count} {what}s.");
            }
            else Note.Fail(st switch
            {
                "timeout" => HookFailures.Friends.SearchNoAnswer,
                "failed:busy" => HookFailures.Busy,
                "failed:bad name" => HookFailures.Friends.BadHandleName,
                "failed:quote" => HookFailures.Friends.QuoteInName,
                "failed:bad hid" => HookFailures.Friends.BadHid,
                "failed:bad character" => HookFailures.Friends.BadCharacterName,
                "failed:no schema" when characters =>
                    HookFailures.Friends.NoSchemaCharacters,
                "failed:no schema" => HookFailures.Friends.NoSchemaHandles,
                _ => HookFailures.Friends.SearchRejected(SocialRequests.FailReason(st)),
            });
        }
        if (_requestToken is null) return;
        if (_social.Settled(snap.SendStateFor(_requestToken), "sent", _requestAt, 250) is not { } rst) return;
        _requestToken = null;
        _social.EditBusy = false;
        if (rst != "sent")
        {
            Note.Fail(rst switch
            {
                "timeout" => HookFailures.Friends.RequestNoAnswer,
                "failed:busy" => HookFailures.Busy,
                "failed:already a friend" => HookFailures.Friends.AlreadyFriend(_requestName),
                "failed:self" => HookFailures.Friends.RequestSelf,
                "failed:-7181" => HookFailures.Friends.IgnoredFirst(_requestName),
                "failed:-7188" or "failed:-516" => HookFailures.FriendListFull,
                "failed:no such result" => HookFailures.Friends.NoSuchResult,
                "failed:no such friend" => HookFailures.Friends.FriendListChangedFor(_requestName),
                "failed:stopped" => HookFailures.Friends.RequestStopped,
                "failed:hourly limit" => HookFailures.Friends.RequestHourlyLimit,
                var s when s.StartsWith("failed:list", StringComparison.Ordinal) =>
                    HookFailures.Friends.RequestListNotSaved,
                var s => HookFailures.Friends.RequestRejected(SocialRequests.FailReason(s)),
            });
            return;
        }
        Note.Show($"Friend request sent to {_requestName}. They'll show on your list{(_requestCharacter ? " under their PlayOnline handle," : "")} as \"Friend request sent\" until they accept.");
        for (int i = 0; i < Hits.Count; i++)
            if (Hits[i].Name == _requestName && Hits[i].Key == _requestKey)
                Hits[i] = Hits[i] with { RequestSent = true };
    }
}
