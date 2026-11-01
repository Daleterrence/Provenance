using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels;

public partial class SocialViewModel
{
    public sealed record ProfileRowItem(string Label, string Value, ImageSource? Picture);

    public ObservableCollection<ProfileRowItem> ProfileRows { get; } = new();
    [ObservableProperty] private bool _profileLoading;
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
    private DateTime _profileAt;
    private readonly Dictionary<string, (PolProfile Profile, DateTime At)> _profileCache = new();
    private ProfilePictureCatalog.Catalog? _picCatalog;
    private bool _picCatalogTried;

    // nothing from the friend row goes in the header, a Private profile hides the name and picture too
    public void RequestProfile(FriendItem f)
    {
        ProfileRows.Clear();
        ProfileName = ProfileHid = ProfileMail = ProfileUpdated = ProfilePrivacy = ProfileAway = ProfileAwayFrom = "";
        ProfilePicture = _avatars.NoProfilePicture;
        _profileFor = f.Name;
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
            _profileToken = null;
            ProfileLoading = false;
            ProfileNote = "";
            ShowProfile(hit.Profile);
            return;
        }
        _profileToken = NewToken();
        _profileKey = f.Key;
        _profileAt = DateTime.UtcNow;
        ProfileLoading = true;
        ProfileNote = "Loading the profile from PlayOnline...";
        if (SocialSnapshot.RequestProfile(pid, _profileToken, f.Row, f.Key)) return;
        _profileToken = null;
        ProfileLoading = false;
        ProfileNote = RequestWriteFailed;
        ProfileNoteIsError = true;
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
        // a "done" with no profile isn't an answer yet
        bool mine = snap.ProfileToken == _profileToken && (snap.ProfileState != "done" || snap.Profile != null);
        if (Settled(mine ? snap.ProfileState : null, "done", _profileAt, 130) is not { } st) return;
        _profileToken = null;
        ProfileLoading = false;
        if (st == "done")
        {
            ProfileNote = "";
            if (_profileKey.Length > 0) _profileCache[_profileKey] = (snap.Profile!, DateTime.UtcNow);
            ShowProfile(snap.Profile!);
            return;
        }
        ProfileNote = st switch
        {
            "timeout" or "failed:timeout" => "PlayOnline didn't answer.",
            "failed:no profile" => "This person doesn't have a PlayOnline profile to show.",
            "failed:no such friend" or "failed:friend list changed" => "Your friend list changed. Try again.",
            "failed:pending" => "You can only view your friends' profiles.",
            "failed:no schema" => "Provenance couldn't read the profile layout from your PlayOnline install, so it can't show profiles.",
            // -0x14cc, which the viewer calls a hidden profile
            "failed:-5324" => ViewerText(0x4ab4, "Profile of %s's handle is hidden.").Replace("%s", Filtered(_profileFor)),
            _ => $"PlayOnline didn't send the profile ({FailReason(st)}).",
        };
        ProfileNoteIsError = st is not ("failed:no profile" or "failed:-5324");
    }

    private void ShowProfile(PolProfile p)
    {
        ProfileName = p.NameState == PolProfile.Private ? StateText(PolProfile.Private) : Filtered(p.Name);
        ProfilePicture = _avatars.NoProfilePicture;
        ProfileHid = p.Hid.Length > 0 ? "H-ID " + p.Hid : "";
        ProfileMail = p.MailState != PolProfile.Shown || p.Mail.Length == 0 ||
                      p.Mail == ViewerText(0x4a7f, "No Mail address") ? "" : Filtered(p.Mail);
        ProfileUpdated = p.Updated is > 0 and < 253402300800   // FromUnixTimeSeconds throws past the year 9999
            ? "Updated " + DateTimeOffset.FromUnixTimeSeconds(p.Updated).ToLocalTime().ToString("d MMM yyyy") : "";
        ProfilePrivacy = p.Level switch
        {
            1 => ViewerText(0x4a7d, "Private"),
            2 => ViewerText(0x4a7e, "Show to friends"),
            3 => ViewerText(0x4a7c, "Show to all"),
            _ => "",
        };
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
