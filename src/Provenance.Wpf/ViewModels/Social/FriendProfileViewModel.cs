using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Hook;
using Provenance.Common.Social;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Social;

public sealed partial class FriendProfileViewModel : ObservableObject
{
    private readonly SocialViewModel _social;

    public FriendProfileViewModel(SocialViewModel social) => _social = social;

    public event Action<string>? Popup;

    public sealed record RowItem(string Label, string Value, ImageSource? Picture);

    public ObservableCollection<RowItem> Rows { get; } = new();
    public ObservableCollection<ProfileTab> Tabs { get; } = new();
    [ObservableProperty] private bool _hasTabs;
    [ObservableProperty] private bool _showsPicture = true;
    [ObservableProperty] private string _gameLine = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TabsClickable))] private bool _loading;
    public bool TabsClickable => !Loading;
    public StatusLine Note { get; } = new();
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _hid = "";
    [ObservableProperty] private string _mail = "";
    [ObservableProperty] private string _updated = "";
    [ObservableProperty] private string _privacy = "";
    [ObservableProperty] private string _away = "";
    [ObservableProperty] private string _awayFrom = "";
    [ObservableProperty] private ImageSource? _picture;
    private sealed class PerLogin
    {
        public readonly HookJobWatch Profile = new(), Ffxi = new();
        public string ProfileKey = "", ProfileFor = "";
        public int ProfileRow = -1;
        public PolProfile? HandleProfile;
        public PolFfxiProfile? CharacterShown;
        public ProfileTab? FfxiFor, FfxiFrom;
        public readonly Dictionary<string, (PolProfile Profile, DateTime At)> ProfileCache = new();
        public string? PopupAfterApply;
    }

    private PerLogin _login = new();

    public void Request(FriendItem f)
    {
        Clear();
        ShowsPicture = true;
        _login.ProfileFor = f.Name;
        _login.ProfileRow = f.Row;
        _login.ProfileKey = f.Key;
        if (!f.IsFriend || f.Pending || _social.Session.Pid is not { } pid)
        {
            Note.Show(HookFailures.Profile.FriendsOnly);
            return;
        }
        if (_social.GameRunning)
        {
            Note.Show(SocialViewModel.GameRunningNote);
            return;
        }
        if (f.Key.Length > 0 && _login.ProfileCache.TryGetValue(f.Key, out var hit) &&
            _social.Now - hit.At < TimeSpan.FromMinutes(5))
        {
            Note.Clear();
            HandleProfileArrived(hit.Profile);
            return;
        }
        var token = _login.Profile.Begin(HookLimits.Profile, _social.Now);
        Loading = true;
        Note.Show("Loading the profile from PlayOnline...");
        if (SocialRequests.RequestProfile(pid, token, f.Row, f.Key)) return;
        _login.Profile.Forget();
        Loading = false;
        Note.Fail(HookFailures.RequestWriteFailed);
    }

    internal void Forget()
    {
        _login.ProfileCache.Clear();
        if (_login.ProfileFor.Length == 0) return;
        Clear();
        Note.Show("Your active handle changed. Open the profile again to see it from this handle.");
    }

    private void Clear()
    {
        Rows.Clear();
        Name = Hid = Mail = Updated = Privacy = Away = AwayFrom = "";
        GameLine = "";
        Picture = _social.Avatars.NoProfilePicture;
        Tabs.Clear();
        HasTabs = false;
        _login.HandleProfile = null;
        _login.CharacterShown = null;
        _login.Profile.Forget();
        _login.Ffxi.Forget();
        Loading = false;
        Note.Soften();
    }

    private string StateText(ProfileFieldState state) => state switch
    {
        ProfileFieldState.Private => _social.Viewer.Text(0x4a73, "Private"),
        ProfileFieldState.UnderTen => _social.Viewer.Text(0x4a75, "Under age ten"),
        _ => _social.Viewer.Text(0x4a81, "Not set"),
    };

    private string ProfilePictureText(int picId)
    {
        var catalog = _social.Viewer.Pictures;
        var pic = catalog?.ProfilePictures.FirstOrDefault(x => x.Id == picId);
        if (pic is null) return _social.Viewer.Text(0x4a73, "Private");
        var cat = catalog!.Categories.FirstOrDefault(c => c.Key == pic.CategoryKey)?.Name ?? "";
        return cat.Length > 0 ? $"{pic.Name} ({cat})" : pic.Name;
    }

    internal void RaisePopup()
    {
        if (_login.PopupAfterApply is not { } text) return;
        _login.PopupAfterApply = null;
        Popup?.Invoke(text);
    }

    internal bool Waiting => _login.Profile.Running || _login.Ffxi.Running;

    internal void Apply(PolSocialSnapshot snap)
    {
        ApplyProfileState(snap);
        ApplyFfxiProfileState(snap);
    }

    internal void Reset()
    {
        _login = new();
        Note.Clear();
        Clear();
        ShowsPicture = true;
    }

    private void ApplyProfileState(PolSocialSnapshot snap)
    {
        if (!_login.Profile.Running) return;
        _login.Profile.WaitingTurn(snap.ProfileJob.Token, snap.ProfileServerBusy, _social.Now);
        bool mine = snap.ProfileJob.Token == _login.Profile.Token && (snap.ProfileJob.State != "done" || snap.Profile != null);
        if (_login.Profile.Settle(mine ? snap.ProfileJob.State : null, "done", _social.Now) is not { } st) return;
        if (st == "timeout") _social.DiscardRequest(HookContract.Requests.Profile);
        Loading = false;
        if (st == "done")
        {
            Note.Clear();
            if (_login.ProfileKey.Length > 0) _login.ProfileCache[_login.ProfileKey] = (snap.Profile!, _social.Now);
            HandleProfileArrived(snap.Profile!);
            return;
        }
        Note.Set(st switch
        {
            "failed:no profile" => HookFailures.Profile.None,
            // -0x14cc, which the viewer calls a hidden profile
            "failed:-5324" => _social.Viewer.Text(0x4ab4, "Profile of %s's handle is hidden.").Replace("%s", _social.Viewer.Filtered(_login.ProfileFor)),
            _ => ProfileFailText(st),
        }, st is not ("failed:no profile" or "failed:-5324"));
    }

    internal static string ProfileFailText(string st) => st switch
    {
        "timeout" => HookFailures.Profile.NoAnswer,
        "failed:busy" => HookFailures.Busy,
        "failed:no such friend" or "failed:friend list changed" => HookFailures.FriendListChanged,
        "failed:pending" => HookFailures.Profile.FriendsOnly,
        "failed:no schema" => HookFailures.Profile.NoSchema,
        NoCharacter => HookFailures.Profile.NoCharacter,
        _ when int.TryParse(SocialRequests.FailReason(st), out _) => HookFailures.Profile.NotSent(SocialRequests.FailReason(st)),
        _ => HookFailures.Profile.NotAsked(SocialRequests.FailReason(st)),
    };

    private const string NoCharacter = "failed:no character";

    private string PrivacyText(PrivacyLevel level) => level switch
    {
        PrivacyLevel.Private => _social.Viewer.Text(0x4a7d, "Private"),
        PrivacyLevel.FriendsOnly => _social.Viewer.Text(0x4a7e, "Show to friends"),
        PrivacyLevel.Everyone => _social.Viewer.Text(0x4a7c, "Show to all"),
        _ => "",
    };

    private void HandleProfileArrived(PolProfile p)
    {
        _login.HandleProfile = p;
        _login.CharacterShown = null;
        Tabs.Clear();
        if (p.Characters.Count > 0 && !_social.IsAnonFriend(_login.ProfileKey))
        {
            Tabs.Add(new ProfileTab(-1, "", _social.Viewer.Text(0x4abe, "Handle Profile"))
            {
                IsSelected = true,
                Picture = HandlePicture(p),
            });
            foreach (var c in p.Characters) Tabs.Add(new ProfileTab(c.Slot, c.Character.Key, TabLabel(c.Character.Key)));
        }
        HasTabs = Tabs.Count > 0;
        ShowProfile(p);
    }

    private ImageSource? HandlePicture(PolProfile p) =>
        p.Rows.FirstOrDefault(r => r.Field == "z_ficon" && r.State == ProfileFieldState.Shown) is { } row
            ? _social.Avatars.ProfilePicture(row.ProfilePictureId) : _social.Avatars.NoProfilePicture;

    private string TabLabel(string characterKey) =>
        !_social.IsAnonFriend(_login.ProfileKey) && _social.CharacterNames.Get(characterKey) is { Name.Length: > 0 } known ? _social.Viewer.Filtered(known.Name) : FfxiProfileText.Title;

    [RelayCommand]
    private void SelectTab(ProfileTab? tab)
    {
        if (tab is null || !tab.Clickable || _login.Ffxi.Running || Loading || !Tabs.Contains(tab)) return;
        if (!tab.IsHandle && _social.IsAnonFriend(_login.ProfileKey)) return;
        if (tab.IsHandle)
        {
            if (_login.HandleProfile is null) return;
            MarkSelected(tab);
            Note.Clear();
            ShowProfile(_login.HandleProfile);
            return;
        }
        if (_social.GameRunning)
        {
            Note.Show(SocialViewModel.GameRunningNote);
            return;
        }
        if (_social.Session.Pid is not { } pid) return;
        if (_login.ProfileKey.Length > 0 && _social.Last.FriendsLoaded)
        {
            if (_social.Last.Friends.FirstOrDefault(x => x.Key == _login.ProfileKey && x.Row >= 0) is not { Pending: false } now)
            {
                Note.Show(HookFailures.Profile.FriendsOnly);
                return;
            }
            _login.ProfileRow = now.Row;
        }
        var from = Tabs.FirstOrDefault(t => t.IsSelected && !t.Failed) ?? _login.FfxiFrom;
        MarkSelected(tab);
        ShowCharacterPage(null);
        var token = _login.Ffxi.Begin(HookLimits.FfxiProfile, _social.Now);
        _login.FfxiFor = tab;
        _login.FfxiFrom = from;
        Loading = true;
        Note.Show(_social.Viewer.Text(0x4a82, "Retrieving profile data..."));
        if (SocialRequests.RequestFfxiProfile(pid, token, _login.ProfileRow, _login.ProfileKey, tab.Slot)) return;
        _login.Ffxi.Forget();
        Loading = false;
        ShowTabAgain(from);
        Note.Fail(HookFailures.RequestWriteFailed);
    }

    private void MarkSelected(ProfileTab tab)
    {
        foreach (var t in Tabs)
        {
            t.IsSelected = t == tab;
            t.Failed = false;
        }
    }

    private void ShowTabAgain(ProfileTab? tab)
    {
        if (tab is null) return;
        MarkSelected(tab);
        if (tab.IsHandle && _login.HandleProfile is not null) ShowProfile(_login.HandleProfile);
        else ShowCharacterPage(_login.CharacterShown?.Slot == tab.Slot ? _login.CharacterShown : null);
    }

    private void ApplyFfxiProfileState(PolSocialSnapshot snap)
    {
        if (!_login.Ffxi.Running) return;
        _login.Ffxi.WaitingTurn(snap.FfxiProfileJob.Token, snap.ProfileServerBusy, _social.Now);
        bool mine = snap.FfxiProfileJob.Token == _login.Ffxi.Token && (snap.FfxiProfileJob.State != "done" || snap.FfxiProfile != null);
        if (_login.Ffxi.Settle(mine ? snap.FfxiProfileJob.State : null, "done", _social.Now) is not { } st) return;
        if (st == "timeout") _social.DiscardRequest(HookContract.Requests.FfxiProfile);
        Loading = false;
        var tab = _login.FfxiFor!;
        if (st == "done" && snap.FfxiProfile!.Character.Key != tab.CharacterKey) st = NoCharacter;
        if (st == "done")
        {
            var r = snap.FfxiProfile!;
            Note.Clear();
            var known = CharacterNames.FromProfile(r);
            if (known is not null && _social.CharacterNames.Get(tab.CharacterKey) != known) _social.CharacterNames.Remember(tab.CharacterKey, known);
            tab.Label = TabLabel(tab.CharacterKey);
            _login.CharacterShown = r with { Slot = tab.Slot };
            ShowCharacterPage(_login.CharacterShown);
            return;
        }
        if (st == "failed:hidden")
        {
            ShowTabAgain(_login.FfxiFrom);
            Note.Clear();
            _login.PopupAfterApply = _social.Viewer.Text(0x4ab6, "Selected profile is hidden.");
            return;
        }
        if (st == NoCharacter) _login.ProfileCache.Remove(_login.ProfileKey);
        Note.Set(st == "failed:no profile" ? _social.Viewer.Text(0x4a83, "No profile data found.") : ProfileFailText(st), st != "failed:no profile");
        tab.Failed = Note.IsError;
    }

    private void ShowCharacterPage(PolFfxiProfile? r)
    {
        ShowsPicture = false;
        Picture = _social.Avatars.NoProfilePicture;
        Updated = Privacy = Away = AwayFrom = "";
        Rows.Clear();
        GameLine = FfxiProfileText.Title;
        if (r is null)
        {
            Name = "";
            return;
        }
        Name = r.NameState == ProfileFieldState.Private ? StateText(ProfileFieldState.Private) : _social.Viewer.Filtered(r.Name);
        foreach (var (label, value) in FfxiProfileText.Rows(r, StateText(ProfileFieldState.Private), StateText(ProfileFieldState.NotSet), _social.Viewer.Filtered))
            Rows.Add(new RowItem(label, value, null));
    }

    private void ShowProfile(PolProfile p)
    {
        ShowsPicture = true;
        GameLine = "";
        Name = p.NameState == ProfileFieldState.Private ? StateText(ProfileFieldState.Private) : _social.Viewer.Filtered(p.Name);
        Picture = HandlePicture(p);
        Hid = p.Hid.Length > 0 ? "H-ID " + p.Hid : "";
        Mail = p.MailState != ProfileFieldState.Shown || p.Mail.Length == 0 ||
                      p.Mail == _social.Viewer.Text(0x4a7f, "No Mail address") ? "" : _social.Viewer.Filtered(p.Mail);
        Updated = p.Updated is > 0 and < 253402300800   // FromUnixTimeSeconds throws past the year 9999
            ? "Updated " + DateTimeOffset.FromUnixTimeSeconds(p.Updated).ToLocalTime().ToString("d MMM yyyy") : "";
        Privacy = PrivacyText(p.Level);
        Rows.Clear();
        foreach (var r in p.Rows)
        {
            bool shown = r.State == ProfileFieldState.Shown;
            if (r.Field == "z_ficon")
            {
                Rows.Add(new RowItem(r.Label,
                                                   shown ? ProfilePictureText(r.ProfilePictureId) : StateText(r.State),
                                                   shown ? _social.Avatars.ProfilePicture(r.ProfilePictureId) : null));
                continue;
            }
            var lines = r.Lines.Where(l => l.Length > 0).Select(_social.Viewer.Filtered).ToList();
            Rows.Add(new RowItem(r.Label, shown && lines.Count > 0
                ? string.Join("\n", lines) : StateText(shown ? ProfileFieldState.NotSet : r.State), null));
        }
        Away = string.Join("\n", p.Away.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        AwayFrom = p.AwayFrom;
    }
}
