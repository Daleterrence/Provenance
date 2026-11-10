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

    private sealed class PerLogin
    {
        public readonly HookJobWatch Search = new(), Request = new(), Answer = new();
        public FriendSearchKind SearchedKind;
        public string SearchedValue = "", RequestName = "", RequestKey = "";
        public bool RequestCharacter;
        public FriendItem? AnswerFor;
        public RequestAnswer AnswerKind;
    }

    private PerLogin _login = new();
    private const int SearchPage = 100;   // the hook fetches the first 100, like the viewer
    private const string TooManyResults = "Too many results. Please narrow your search.";


    internal bool Writing => _login.Answer.Running || _login.Request.Running;

    internal void Reset()
    {
        _login = new();
        Busy = false;
        Note.Clear();
        Hits.Clear();
    }

    public void Run(string text)
    {
        var value = text.Trim();
        var kind = Kind;
        if (Busy || !_social.LoggedIn || _social.Session.Pid is not { } pid) return;
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
        var token = _login.Search.Begin(HookLimits.Search, _social.Now);
        _login.SearchedKind = kind;
        _login.SearchedValue = value;
        Hits.Clear();
        Busy = true;
        Note.Show("Searching PlayOnline...");
        if (SocialRequests.Search(pid, token, kind, value)) return;
        _login.Search.Forget();
        Busy = false;
        Note.Fail(HookFailures.RequestWriteFailed);
    }

    // a search nobody's waiting on any more: withdrawn if the hook hasn't taken it yet, and its answer's ignored
    public void Cancel()
    {
        if (!_login.Search.Running) return;
        _social.DiscardRequest(HookContract.Requests.Search);
        _login.Search.Forget();
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
        if (hit.IsFriend || hit.RequestSent || _login.Request.Running || !_social.BeginEdit(out var pid, out var token, followSend: true)) return;
        _login.Request.Begin(token, HookLimits.FriendRequest, _social.Now);
        _login.RequestName = hit.Name;
        _login.RequestKey = hit.Key;
        _login.RequestCharacter = hit.IsCharacter;
        Note.Show($"Sending a friend request to {hit.Name}...");
        if (SocialRequests.SendFriendRequest(pid, token, hit.Index, hit.Key, _social.Viewer.Text(0x46c9, "Let's be friends!"),
                                             _social.Viewer.Text(0x46e6, "Would you like to be friends?"))) return;
        _login.Request.Forget();
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
        _login.Answer.Begin(token, HookLimits.Answer, _social.Now);
        _login.AnswerFor = f;
        _login.AnswerKind = answer;
        var subject = answer == RequestAnswer.Accept
            ? _social.Viewer.Text(0x46d0, "Friend registration accepted")
            : _social.Viewer.Text(0x46d1, "Friend registration declined");
        // ignoring from a request only ever covers this handle, same as the viewer
        if (SocialRequests.AnswerRequest(pid, answer, token, f.RequestMid, subject, allHandles: false)) return;
        _login.Answer.Forget();
        _login.AnswerFor = null;
        _social.WriteOrFail(false);
    }

    internal void Apply(PolSocialSnapshot snap)
    {
        ApplyAnswerState(snap);
        ApplySearchState(snap);
    }

    private void ApplyAnswerState(PolSocialSnapshot snap)
    {
        if (!_login.Answer.Running || _login.AnswerFor is not { } f) return;
        if (_login.Answer.Settle(snap.SendStateFor(_login.Answer.Token), "sent", _social.Now) is not { } st) return;
        _login.AnswerFor = null;
        _social.EditBusy = false;
        if (st == "sent")
        {
            _social.Answered(f.RequestMid, ack: true);
            _social.Chat?.AddNote(f.Name, f.Key, _login.AnswerKind switch
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
        _login.Search.WaitingTurn(snap.SearchJob.Token, snap.ProfileServerBusy, _social.Now);
        if (_login.Search.Running && _login.Search.Settle(snap.SearchJob.StateFor(_login.Search.Token), "done", _social.Now) is { } st)
        {
            if (st == "timeout") _social.DiscardRequest(HookContract.Requests.Search);
            Busy = false;
            var characters = _login.SearchedKind == FriendSearchKind.Character;
            if (st == "done" && snap.Search.TooMany) Note.Fail(TooManyResults);
            else if (st == "done")
            {
                Hits.Clear();
                foreach (var h in snap.Search.Hits)
                {
                    // the search matched INITCAP of what was typed, so a hit with no name in it is still called that
                    var name = characters && h.Name.Length == 0 ? FriendSearch.InitCap(_login.SearchedValue) : h.Name;
                    Hits.Add(new SearchHitItem(h.Index, name, _social.Avatars.ProfilePicture(characters ? 0 : h.ProfilePictureId),
                                               h.IsFriend, h.Key, IsMe: h.IsMe)
                             { IsCharacter = characters, World = characters ? h.World : "" });
                }
                var what = characters ? "character" : "handle";
                // only past the first page were there more than came back, the hook drops a hit with nothing in it
                var total = snap.Search.Total > SearchPage ? snap.Search.Total : Hits.Count;
                Note.Show(Hits.Count == 0 ? _login.SearchedKind switch
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
        if (!_login.Request.Running) return;
        if (_login.Request.Settle(snap.SendStateFor(_login.Request.Token), "sent", _social.Now) is not { } rst) return;
        _social.EditBusy = false;
        if (rst != "sent")
        {
            Note.Fail(rst switch
            {
                "timeout" => HookFailures.Friends.RequestNoAnswer,
                "failed:busy" => HookFailures.Busy,
                "failed:already a friend" => HookFailures.Friends.AlreadyFriend(_login.RequestName),
                "failed:self" => HookFailures.Friends.RequestSelf,
                "failed:-7181" => HookFailures.Friends.IgnoredFirst(_login.RequestName),
                "failed:-7188" or "failed:-516" => HookFailures.FriendListFull,
                "failed:no such result" => HookFailures.Friends.NoSuchResult,
                "failed:no such friend" => HookFailures.Friends.FriendListChangedFor(_login.RequestName),
                "failed:stopped" => HookFailures.Friends.RequestStopped,
                "failed:hourly limit" => HookFailures.Friends.RequestHourlyLimit,
                var s when s.StartsWith("failed:list", StringComparison.Ordinal) =>
                    HookFailures.Friends.RequestListNotSaved,
                var s => HookFailures.Friends.RequestRejected(SocialRequests.FailReason(s)),
            });
            return;
        }
        Note.Show($"Friend request sent to {_login.RequestName}. They'll show on your list{(_login.RequestCharacter ? " under their PlayOnline handle," : "")} as \"Friend request sent\" until they accept.");
        for (int i = 0; i < Hits.Count; i++)
            if (Hits[i].Name == _login.RequestName && Hits[i].Key == _login.RequestKey)
                Hits[i] = Hits[i] with { RequestSent = true };
    }
}
