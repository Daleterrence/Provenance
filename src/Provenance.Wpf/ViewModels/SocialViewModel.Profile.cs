using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels;

// Handle Profile (Slot -1) or one of the handle's FFXI characters
public sealed partial class ProfileTab(int slot, string characterKey, string label) : ObservableObject
{
    public int Slot { get; } = slot;
    public string CharacterKey { get; } = characterKey;
    [ObservableProperty] private string _label = label;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Clickable))] private bool _isSelected;
    // a tab whose read failed can be clicked again to retry
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Clickable))] private bool _failed;
    public bool Clickable => !IsSelected || Failed;
}

public partial class SocialViewModel
{
    public sealed record ProfileRowItem(string Label, string Value, ImageSource? Picture);

    public ObservableCollection<ProfileRowItem> ProfileRows { get; } = new();
    public ObservableCollection<ProfileTab> ProfileTabs { get; } = new();
    [ObservableProperty] private bool _hasProfileTabs;
    [ObservableProperty] private bool _profileShowsPicture = true;
    [ObservableProperty] private string _profileGameLine = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProfileTabsClickable))] private bool _profileLoading;
    public bool ProfileTabsClickable => !ProfileLoading;
    [ObservableProperty] private string _profileNote = "";
    [ObservableProperty] private bool _profileNoteIsError;
    partial void OnProfileNoteChanged(string value) => ProfileNoteIsError = false;
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _profileHid = "";
    [ObservableProperty] private string _profileMail = "";
    [ObservableProperty] private string _profileUpdated = "";
    [ObservableProperty] private string _profilePrivacy = "";
    [ObservableProperty] private string _profileAway = "";
    [ObservableProperty] private string _profileAwayFrom = "";
    [ObservableProperty] private ImageSource? _profilePicture;
    private string? _profileToken;
    private string _profileKey = "", _profileFor = "";
    private int _profileRow = -1;
    private DateTime _profileAt;
    // what's on screen: the handle's answer and the last character one that loaded, so going back never reads again
    private PolProfile? _handleProfile;
    private PolFfxiProfile? _characterShown;
    private string? _ffxiToken;
    private DateTime _ffxiAt;
    private ProfileTab? _ffxiFor, _ffxiFrom;
    private readonly Dictionary<string, (PolProfile Profile, DateTime At)> _profileCache = new();
    private ProfilePictureCatalog.Catalog? _picCatalog;
    private bool _picCatalogTried;

    // nothing from the friend row goes in the header, a Private profile hides the name and picture too
    public void RequestProfile(FriendItem f)
    {
        ProfileRows.Clear();
        ProfileName = ProfileHid = ProfileMail = ProfileUpdated = ProfilePrivacy = ProfileAway = ProfileAwayFrom = "";
        ProfileGameLine = "";
        ProfilePicture = _avatars.NoProfilePicture;
        ProfileShowsPicture = true;
        ProfileTabs.Clear();
        HasProfileTabs = false;
        _handleProfile = null;
        _characterShown = null;
        _profileToken = _ffxiToken = null;
        ProfileLoading = false;
        _profileFor = f.Name;
        _profileRow = f.Row;
        _profileKey = f.Key;
        // a request with no answer yet has no profile to show (the viewer greys it out)
        if (!f.IsFriend || f.Pending || _pol.Pid is not { } pid)
        {
            ProfileNote = "You can only view your friends' profiles.";
            return;
        }
        if (GameRunning)
        {
            ProfileNote = GameRunningNote;
            return;
        }
        // the same profile again within 5 mins shows the copy we've got, no second request for a page just read
        if (f.Key.Length > 0 && _profileCache.TryGetValue(f.Key, out var hit) &&
            DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5))
        {
            ProfileNote = "";
            HandleProfileArrived(hit.Profile);
            return;
        }
        _profileToken = NewToken();
        _profileAt = DateTime.UtcNow;
        ProfileLoading = true;
        ProfileNote = "Loading the profile from PlayOnline...";
        if (SocialSnapshot.RequestProfile(pid, _profileToken, f.Row, f.Key)) return;
        _profileToken = null;
        ProfileLoading = false;
        ProfileNote = RequestWriteFailed;
        ProfileNoteIsError = true;
    }

    // after a switch: nothing read under the old handle stays on screen or in the cache
    private void ForgetProfiles()
    {
        _profileCache.Clear();
        if (_profileFor.Length == 0) return;
        ProfileRows.Clear();
        ProfileName = ProfileHid = ProfileMail = ProfileUpdated = ProfilePrivacy = ProfileAway = ProfileAwayFrom = "";
        ProfileGameLine = "";
        ProfilePicture = _avatars.NoProfilePicture;
        ProfileTabs.Clear();
        HasProfileTabs = false;
        _handleProfile = null;
        _characterShown = null;
        _profileToken = _ffxiToken = null;
        ProfileLoading = false;
        ProfileNoteIsError = false;
        ProfileNote = "Your active handle changed. Open the profile again to see it from this handle.";
    }

    private string StateText(int state) => state switch
    {
        PolProfile.Private => ViewerText(0x4a73, "Private"),
        PolProfile.UnderTen => ViewerText(0x4a75, "Under age ten"),
        _ => ViewerText(0x4a81, "Not set"),
    };

    // "name (category)" like the viewer's Portrait row
    private string ProfilePictureText(int picId)
    {
        if (!_picCatalogTried)
        {
            _picCatalogTried = true;
            var viewer = ViewerFolder();
            _picCatalog = viewer is null ? null : ProfilePictureCatalog.Load(viewer);
        }
        var pic = _picCatalog?.ProfilePictures.FirstOrDefault(x => x.Id == picId);
        if (pic is null) return ViewerText(0x4a73, "Private");
        var cat = _picCatalog!.Categories.FirstOrDefault(c => c.Key == pic.CategoryKey)?.Name ?? "";
        return cat.Length > 0 ? $"{pic.Name} ({cat})" : pic.Name;
    }

    private void ApplyProfileState(PolSocialSnapshot snap)
    {
        if (_profileToken is null) return;
        WaitingTurn(ref _profileAt, _profileToken, snap.ProfileToken, snap);
        // a "done" with no profile isn't an answer yet
        bool mine = snap.ProfileToken == _profileToken && (snap.ProfileState != "done" || snap.Profile != null);
        if (Settled(mine ? snap.ProfileState : null, "done", _profileAt, 130) is not { } st) return;
        if (st == "timeout") Withdraw("profile");
        _profileToken = null;
        ProfileLoading = false;
        if (st == "done")
        {
            ProfileNote = "";
            if (_profileKey.Length > 0) _profileCache[_profileKey] = (snap.Profile!, DateTime.UtcNow);
            HandleProfileArrived(snap.Profile!);
            return;
        }
        ProfileNote = st switch
        {
            "failed:no profile" => "This person doesn't have a PlayOnline profile to show.",
            // -0x14cc, which the viewer calls a hidden profile
            "failed:-5324" => ViewerText(0x4ab4, "Profile of %s's handle is hidden.").Replace("%s", Filtered(_profileFor)),
            _ => ProfileFailText(st),
        };
        ProfileNoteIsError = st is not ("failed:no profile" or "failed:-5324");
    }

    private static string ProfileFailText(string st) => st switch
    {
        "timeout" or "failed:timeout" => "PlayOnline didn't answer.",
        "failed:no such friend" or "failed:friend list changed" => "Your friend list changed. Try again.",
        "failed:pending" => "You can only view your friends' profiles.",
        "failed:no schema" => "Provenance couldn't read the profile layout from your PlayOnline install, so it can't show profiles.",
        "failed:account notice" => "This PlayOnline account can't view profiles right now.",
        NoCharacter => "Provenance couldn't find that character. Open the profile again from your friends list.",
        // only a number is PlayOnline's own error, the rest never got as far as asking
        _ when int.TryParse(FailReason(st), out _) => $"PlayOnline didn't send the profile ({FailReason(st)}).",
        _ => $"Provenance couldn't ask PlayOnline for the profile ({FailReason(st)}).",
    };

    private const string NoCharacter = "failed:no character";

    private string PrivacyText(int level) => level switch
    {
        1 => ViewerText(0x4a7d, "Private"),
        2 => ViewerText(0x4a7e, "Show to friends"),
        3 => ViewerText(0x4a7c, "Show to all"),
        _ => "",
    };

    // the tabs only show when there's a character to put in them
    private void HandleProfileArrived(PolProfile p)
    {
        _handleProfile = p;
        _characterShown = null;
        ProfileTabs.Clear();
        if (p.Characters.Count > 0)
        {
            ProfileTabs.Add(new ProfileTab(-1, "", ViewerText(0x4abe, "Handle Profile")) { IsSelected = true });
            foreach (var c in p.Characters) ProfileTabs.Add(new ProfileTab(c.Slot, c.CharacterKey, TabLabel(c.CharacterKey)));
        }
        HasProfileTabs = ProfileTabs.Count > 0;
        ShowProfile(p);
    }

    private string TabLabel(string characterKey) =>
        CharacterNames.Get(characterKey) is { Name.Length: > 0 } known ? Filtered(known.Name) : FfxiProfileText.Title;

    // one read per click, never for the tab that's already up or while one's out. Handle Profile shows the answer it had
    [RelayCommand]
    private void SelectProfileTab(ProfileTab? tab)
    {
        if (tab is null || !tab.Clickable || _ffxiToken is not null || ProfileLoading || !ProfileTabs.Contains(tab)) return;
        if (tab.Slot < 0)
        {
            if (_handleProfile is null) return;
            SelectTab(tab);
            ProfileNote = "";
            ShowProfile(_handleProfile);
            return;
        }
        if (GameRunning)
        {
            ProfileNote = GameRunningNote;
            return;
        }
        if (_pol.Pid is not { } pid) return;
        // the row from the latest friend list, a friend added or removed above this one moves it
        if (_profileKey.Length > 0 && _last.FriendsLoaded)
        {
            if (_last.Friends.FirstOrDefault(x => x.Key == _profileKey && x.Row >= 0) is not { Pending: false } now)
            {
                ProfileNote = "You can only view your friends' profiles.";
                return;
            }
            _profileRow = now.Row;
        }
        var from = ProfileTabs.FirstOrDefault(t => t.IsSelected && !t.Failed) ?? _ffxiFrom;
        SelectTab(tab);
        ShowCharacterPage(null);
        _ffxiToken = NewToken();
        _ffxiAt = DateTime.UtcNow;
        _ffxiFor = tab;
        _ffxiFrom = from;
        ProfileLoading = true;
        ProfileNote = ViewerText(0x4a82, "Retrieving profile data...");
        if (SocialSnapshot.RequestFfxiProfile(pid, _ffxiToken, _profileRow, _profileKey, tab.Slot)) return;
        _ffxiToken = null;
        ProfileLoading = false;
        ShowTabAgain(from);
        ProfileNote = RequestWriteFailed;
        ProfileNoteIsError = true;
    }

    private void SelectTab(ProfileTab tab)
    {
        foreach (var t in ProfileTabs)
        {
            t.IsSelected = t == tab;
            t.Failed = false;
        }
    }

    // a tab that was up before, from what it had, no new read
    private void ShowTabAgain(ProfileTab? tab)
    {
        if (tab is null) return;
        SelectTab(tab);
        if (tab.Slot < 0 && _handleProfile is not null) ShowProfile(_handleProfile);
        else ShowCharacterPage(_characterShown?.Slot == tab.Slot ? _characterShown : null);
    }

    private void ApplyFfxiProfileState(PolSocialSnapshot snap)
    {
        if (_ffxiToken is null) return;
        WaitingTurn(ref _ffxiAt, _ffxiToken, snap.FfxiProfileToken, snap);
        bool mine = snap.FfxiProfileToken == _ffxiToken && (snap.FfxiProfileState != "done" || snap.FfxiProfile != null);
        if (Settled(mine ? snap.FfxiProfileState : null, "done", _ffxiAt, 130) is not { } st) return;
        if (st == "timeout") Withdraw("ffxiprofile");
        _ffxiToken = null;
        ProfileLoading = false;
        var tab = _ffxiFor!;
        // the hook read whatever's in that slot now, which has to be the character the tab's for
        if (st == "done" && snap.FfxiProfile!.CharacterKey != tab.CharacterKey) st = NoCharacter;
        if (st == "done")
        {
            var r = snap.FfxiProfile!;
            ProfileNote = "";
            var known = CharacterNames.FromProfile(r);
            if (known is not null && CharacterNames.Get(tab.CharacterKey) != known) CharacterNames.Remember(tab.CharacterKey, known);
            tab.Label = TabLabel(tab.CharacterKey);
            _characterShown = r with { Slot = tab.Slot };
            ShowCharacterPage(_characterShown);
            return;
        }
        if (st == "failed:hidden")
        {
            ShowTabAgain(_ffxiFrom);
            ProfileNote = ViewerText(0x4ab6, "Selected profile is hidden.");
            return;
        }
        // a profile shown again from the copy has slots the hook's lost, so opening it again reads it afresh
        if (st == NoCharacter) _profileCache.Remove(_profileKey);
        ProfileNote = st == "failed:no profile" ? ViewerText(0x4a83, "No profile data found.") : ProfileFailText(st);
        ProfileNoteIsError = st != "failed:no profile";
        tab.Failed = ProfileNoteIsError;
    }

    // the handle's H-ID and mail stay, there's no picture and the record level goes after the game's name
    private void ShowCharacterPage(PolFfxiProfile? r)
    {
        ProfileShowsPicture = false;
        ProfilePicture = _avatars.NoProfilePicture;
        ProfileUpdated = ProfilePrivacy = ProfileAway = ProfileAwayFrom = "";
        ProfileRows.Clear();
        if (r is null)
        {
            ProfileName = "";
            ProfileGameLine = FfxiProfileText.Title;
            return;
        }
        ProfileName = r.NameState == PolProfile.Private ? StateText(PolProfile.Private) : Filtered(r.Name);
        ProfileGameLine = FfxiProfileText.Line(PrivacyText(r.Level));
        foreach (var (label, value) in FfxiProfileText.Rows(r, StateText(PolProfile.Private), StateText(PolProfile.NotSet), Filtered))
            ProfileRows.Add(new ProfileRowItem(label, value, null));
    }

    private void ShowProfile(PolProfile p)
    {
        ProfileShowsPicture = true;
        ProfileGameLine = "";
        ProfileName = p.NameState == PolProfile.Private ? StateText(PolProfile.Private) : Filtered(p.Name);
        ProfilePicture = _avatars.NoProfilePicture;
        ProfileHid = p.Hid.Length > 0 ? "H-ID " + p.Hid : "";
        ProfileMail = p.MailState != PolProfile.Shown || p.Mail.Length == 0 ||
                      p.Mail == ViewerText(0x4a7f, "No Mail address") ? "" : Filtered(p.Mail);
        ProfileUpdated = p.Updated is > 0 and < 253402300800   // FromUnixTimeSeconds throws past the year 9999
            ? "Updated " + DateTimeOffset.FromUnixTimeSeconds(p.Updated).ToLocalTime().ToString("d MMM yyyy") : "";
        ProfilePrivacy = PrivacyText(p.Level);
        ProfileRows.Clear();
        foreach (var r in p.Rows)
        {
            bool shown = r.State == PolProfile.Shown;
            if (r.Field == "z_ficon")
            {
                if (shown) ProfilePicture = _avatars.ProfilePicture(r.ProfilePictureId);
                ProfileRows.Add(new ProfileRowItem(r.Label,
                                                   shown ? ProfilePictureText(r.ProfilePictureId) : StateText(r.State),
                                                   shown ? _avatars.ProfilePicture(r.ProfilePictureId) : null));
                continue;
            }
            var lines = r.Lines.Where(l => l.Length > 0).Select(Filtered).ToList();
            ProfileRows.Add(new ProfileRowItem(r.Label, shown && lines.Count > 0
                ? string.Join("\n", lines) : StateText(shown ? PolProfile.NotSet : r.State), null));
        }
        ProfileAway = string.Join("\n", p.Away.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        ProfileAwayFrom = p.AwayFrom;
    }
}
