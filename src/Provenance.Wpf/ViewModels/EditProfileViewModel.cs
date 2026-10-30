using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Social;

namespace Provenance.Wpf.ViewModels;

public sealed record ProfileChoice(long Code, string Text)
{
    public override string ToString() => Text;
}

// only a full pick counts, and an unknown code's kept until something's picked
public sealed partial class CascadePick : ObservableObject
{
    private readonly IReadOnlyList<MyProfileChoice> _rows;
    private readonly int _levels;
    private readonly string _notSet;
    private long _initial;
    private bool _touched, _setting;

    public CascadePick(IReadOnlyList<MyProfileChoice> rows, int levels, string notSet)
    {
        _rows = rows;
        _levels = levels;
        _notSet = notSet;
        Tops = [notSet, .. rows.Select(r => Part(r, 0)).Where(p => p.Length > 0).Distinct()];
    }

    public event Action? Changed;

    public IReadOnlyList<string> Tops { get; }
    public ObservableCollection<string> Mids { get; } = new();
    public ObservableCollection<ProfileChoice> Leaves { get; } = new();
    public bool HasMid => _levels >= 3;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSet))] private string? _top;
    [ObservableProperty] private string? _mid;
    [ObservableProperty] private ProfileChoice? _leaf;

    public bool IsSet => Top is { } t && t != _notSet;

    // 0 for Not set, a table code, or null while the pick's half done
    public long? Code => !_touched ? _initial : !IsSet ? 0 : Leaf?.Code;

    public void Set(long code)
    {
        _initial = code;
        _touched = false;
        _setting = true;
        try
        {
            var row = code == 0 ? null : _rows.FirstOrDefault(r => r.Code == code);
            Top = row is null ? _notSet : Part(row, 0);
            if (row != null && HasMid) Mid = Part(row, 1);
            Leaf = row is null ? null : Leaves.FirstOrDefault(l => l.Code == code);
        }
        finally { _setting = false; }
    }

    private static string Part(MyProfileChoice r, int i) => i < r.Parts.Count ? r.Parts[i].Trim() : "";

    partial void OnTopChanged(string? value)
    {
        Mids.Clear();
        Leaves.Clear();
        Mid = null;
        Leaf = null;
        if (IsSet && HasMid)
            foreach (var m in _rows.Where(r => Part(r, 0) == value).Select(r => Part(r, 1)).Distinct()) Mids.Add(m);
        else if (IsSet)
            FillLeaves(_rows.Where(r => Part(r, 0) == value), 1);
        Touch();
    }

    partial void OnMidChanged(string? value)
    {
        if (!HasMid) return;
        Leaves.Clear();
        Leaf = null;
        if (value != null) FillLeaves(_rows.Where(r => Part(r, 0) == Top && Part(r, 1) == value), 2);
        Touch();
    }

    partial void OnLeafChanged(ProfileChoice? value) => Touch();

    private void FillLeaves(IEnumerable<MyProfileChoice> rows, int part)
    {
        foreach (var r in rows) Leaves.Add(new ProfileChoice(r.Code, Part(r, part)));
        if (Leaves.Count == 1 && !_setting) Leaf = Leaves[0];   // nothing to choose between
    }

    private void Touch()
    {
        if (_setting) return;
        _touched = true;
        Changed?.Invoke();
    }
}

// only changed fields get sent, and a rename goes after the save
public sealed partial class EditProfileViewModel : ObservableObject, IDisposable
{
    private enum Job { None, Load, Save, Rename }

    private readonly SocialViewModel _social;
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Job _job;
    private string? _token;
    private int _pid;
    private DateTime _at;
    private string _noMail = "No Mail address";
    private string _savedHandle = "";
    private string _savedMail = "";
    private int _savedLevel;
    private readonly Dictionary<string, long> _saved = new();
    private bool _renameAfterSave;
    private bool _loadingValues;

    public EditProfileViewModel(SocialViewModel social)
    {
        _social = social;
        _social.SnapshotApplied += OnSnapshot;
        _timer.Tick += (_, _) => CheckTimeout();
        NotSet = social.ViewerText(0x4a81, "Not set");
    }

    public SocialViewModel Social => _social;
    public string NotSet { get; }
    public IReadOnlyList<ProfileChoice> VisibilityChoices { get; } =
        [new(3, "Everyone on PlayOnline"), new(2, "Friends only"), new(1, "Nobody (private)")];

    [ObservableProperty] private IReadOnlyList<ProfileChoice> _sexChoices = [];
    [ObservableProperty] private IReadOnlyList<ProfileChoice> _jobChoices = [];
    [ObservableProperty] private IReadOnlyList<ProfileChoice> _languageChoices = [];
    [ObservableProperty] private CascadePick? _location;
    [ObservableProperty] private CascadePick? _interest1;
    [ObservableProperty] private CascadePick? _interest2;
    [ObservableProperty] private CascadePick? _interest3;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditFields), nameof(CanRetryLoad))] private bool _isLoaded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditFields), nameof(FooterText), nameof(FooterIsError), nameof(CanRetryLoad))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _busy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FooterText), nameof(FooterIsError))] private string _note = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FooterIsError), nameof(CanRetryLoad))]
    private bool _noteIsError;

    // puts the window's loading overlay up. Only the load gets one, a save leaves the form in view
    [ObservableProperty] private bool _loadingProfile;

    public string FooterText => !Busy && Problem.Length > 0 ? Problem : Note;
    public bool FooterIsError => !Busy && Problem.Length > 0 || NoteIsError;
    public bool CanEditFields => IsLoaded && !Busy;
    public bool CanRetryLoad => !IsLoaded && !Busy && NoteIsError;

    [RelayCommand]
    private void RetryLoad() => Load();

    [ObservableProperty] private long _level;   // 1 private, 2 friends, 3 everyone, 0 if it loaded as none of them
    [ObservableProperty] private string _handleName = "";
    [ObservableProperty] private string _mail = "";
    [ObservableProperty] private string _ageText = "";
    [ObservableProperty] private bool _approximateAge;
    [ObservableProperty] private long _sex;
    [ObservableProperty] private long _job1;
    [ObservableProperty] private long _lang0;
    [ObservableProperty] private long _lang1;
    [ObservableProperty] private long _lang2;

    partial void OnLevelChanged(long value) => Dirty();
    partial void OnHandleNameChanged(string value) => Dirty();
    partial void OnMailChanged(string value) => Dirty();
    partial void OnAgeTextChanged(string value) => Dirty();
    partial void OnApproximateAgeChanged(bool value) => Dirty();
    partial void OnSexChanged(long value) => Dirty();
    partial void OnJob1Changed(long value) => Dirty();
    partial void OnLang0Changed(long value) => Dirty();
    partial void OnLang1Changed(long value) => Dirty();
    partial void OnLang2Changed(long value) => Dirty();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(FooterText), nameof(FooterIsError))]
    private string _problem = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _hasChanges;

    private void Dirty()
    {
        if (_loadingValues || !IsLoaded) return;
        Problem = Check();
        HasChanges = ChangedFields().Count > 0 || Level != _savedLevel || HandleChanged;
        SaveCommand.NotifyCanExecuteChanged();
        if (!Busy && !NoteIsError) Note = "";
    }

    private bool HandleChanged => HandleName.Trim() != _savedHandle;

    // the viewer's checks that work here, the hook and the server check again anyway
    private string Check()
    {
        var name = HandleName.Trim();
        if (name.Length == 0) return "Enter a handle name.";
        if (name.Length > 15) return "A handle name can be 15 characters at most.";
        if (name.Any(c => c < 0x20 || c > 0x7e)) return "A handle name can only use English letters, numbers and symbols.";
        var age = AgeText.Trim();
        if (age.Length > 0 && !(age.All(char.IsAsciiDigit) && age.Length <= 3)) return "Age is a number from 1 to 999.";
        if (Mail.Trim().Length > 319) return "The mail address is too long.";
        // it loads masked, so an edited copy of the mask would save as the new address
        if (Mail.Trim() != _savedMail && Mail.Contains("***")) return "Type your whole mail address to change it.";
        var langs = new[] { Lang0, Lang1, Lang2 }.Where(x => x != 0).ToList();
        if (langs.Count != langs.Distinct().Count()) return "Choose each language once.";
        if (Location is { } l && l.Code is null)
            return l.Mid is null ? "Choose the country and area of your location." : "Choose the area of your location.";
        foreach (var (pick, n) in new[] { (Interest1, 1), (Interest2, 2), (Interest3, 3) })
            if (pick is { } p && p.Code is null) return $"Choose an item for interest {n}.";
        return "";
    }

    // the fields that differ from POL's copy, as the hook's name=value lines
    private List<KeyValuePair<string, string>> ChangedFields()
    {
        (string Field, long? Value)[] nums =
        [
            ("z_age", int.TryParse(AgeText.Trim(), out var age) ? age : 0), ("z_age_f", ApproximateAge ? 1 : 0),
            ("z_sex", Sex), ("z_job", Job1), ("z_area", Location?.Code), ("z_lang0", Lang0), ("z_lang1", Lang1),
            ("z_lang2", Lang2), ("z_fav0", Interest1?.Code), ("z_fav1", Interest2?.Code), ("z_fav2", Interest3?.Code),
        ];
        var list = new List<KeyValuePair<string, string>>();
        // a field the loaded record lacks counts as 0 (Not set), so it's only sent once it's set
        foreach (var (field, value) in nums)
            if (value is { } v && (_saved.TryGetValue(field, out var was) ? was : 0) != v)
                list.Add(new(field, v.ToString()));
        var mail = Mail.Trim();
        if (mail != _savedMail) list.Add(new("z_mail", mail.Length == 0 ? _noMail : mail));
        return list;
    }

    public void Load()
    {
        if (_social.SessionPid is not { } pid)
        {
            Fail("Log in to edit your profile.");
            return;
        }
        if (_social.GameRunning)
        {
            Fail(SocialViewModel.GameRunningNote);
            return;
        }
        Start(Job.Load);
        if (!SocialSnapshot.RequestMyProfile(pid, _token!)) Fail(WriteFailed);
    }

    private void Start(Job job, string note = "")
    {
        _job = job;
        _token = Guid.NewGuid().ToString("N")[..12];
        _pid = _social.SessionPid ?? 0;
        _at = DateTime.UtcNow;
        LoadingProfile = job == Job.Load;
        Busy = true;
        NoteIsError = false;
        Note = note;
        _timer.Start();
    }

    private const string WriteFailed = "Provenance couldn't send the request to PlayOnline. Try again.";
    private const string SaveUnconfirmed = "PlayOnline didn't confirm the save. Open your profile again to see what it kept.";

    private void EndJob()
    {
        _timer.Stop();
        _job = Job.None;
        _token = null;
        LoadingProfile = false;
        Busy = false;
    }

    private void Fail(string text)
    {
        _renameAfterSave = false;
        EndJob();
        NoteIsError = true;
        Note = text;
    }

    private string? JobExt => _job switch
    {
        Job.Load => "myprofile",
        Job.Save => "saveprofile",
        Job.Rename => "rename",
        _ => null,
    };

    // withdraws a request the hook hasn't taken yet, so it never runs later
    private void Abandon(string text)
    {
        if (JobExt is { } ext && _pid != 0) SocialSnapshot.CancelRequest(_pid, ext);
        Fail(text);
    }

    // the hook times out on its own (120 secs a slot and job, 240 + 30 for a rename), this catches one gone quiet
    private void CheckTimeout()
    {
        if (_job == Job.None)
        {
            _timer.Stop();
            return;
        }
        if (_social.SessionPid != _pid)
        {
            Abandon("Your PlayOnline session ended, so the request stopped.");
            return;
        }
        if (DateTime.UtcNow - _at > TimeSpan.FromSeconds(_job == Job.Rename ? 290 : 250))
            Abandon(_job == Job.Load ? "PlayOnline didn't answer." : SaveUnconfirmed);
    }

    private void Fill(MyProfile p)
    {
        _loadingValues = true;
        try
        {
            _noMail = _social.ViewerText(0x4a7f, "No Mail address");
            IReadOnlyList<MyProfileChoice> T(string n) => p.Tables.TryGetValue(n, out var t) ? t : [];
            List<ProfileChoice> One(string n) =>
                [new(0, NotSet), .. T(n).Select(r => new ProfileChoice(r.Code, r.Parts.LastOrDefault()?.Trim() ?? ""))];
            SexChoices = One("z_sex");
            JobChoices = One("z_job");
            LanguageChoices = One("z_lang0");
            Location = new CascadePick(T("z_area"), 3, NotSet);
            Interest1 = new CascadePick(T("z_fav0"), 2, NotSet);
            Interest2 = new CascadePick(T("z_fav0"), 2, NotSet);
            Interest3 = new CascadePick(T("z_fav0"), 2, NotSet);
            foreach (var c in new[] { Location, Interest1, Interest2, Interest3 }) c.Changed += Dirty;

            _saved.Clear();
            foreach (var (name, f) in p.Fields) if (name != "z_mail") _saved[name] = f.Value;
            long V(string n) => _saved.TryGetValue(n, out var v) ? v : 0;
            // a code this install doesn't know shows "Not set" like the viewer, and it's kept so a save can't clear it
            long Keep(IReadOnlyList<ProfileChoice> list, long v, Action<IReadOnlyList<ProfileChoice>> set)
            {
                if (v != 0 && list.All(c => c.Code != v)) set(list.Append(new ProfileChoice(v, NotSet)).ToList());
                return v;
            }
            Sex = Keep(SexChoices, V("z_sex"), l => SexChoices = l);
            Job1 = Keep(JobChoices, V("z_job"), l => JobChoices = l);
            Lang0 = Keep(LanguageChoices, V("z_lang0"), l => LanguageChoices = l);
            Lang1 = Keep(LanguageChoices, V("z_lang1"), l => LanguageChoices = l);
            Lang2 = Keep(LanguageChoices, V("z_lang2"), l => LanguageChoices = l);
            Location.Set(V("z_area"));
            Interest1.Set(V("z_fav0"));
            Interest2.Set(V("z_fav1"));
            Interest3.Set(V("z_fav2"));
            AgeText = V("z_age") > 0 ? V("z_age").ToString() : "";
            ApproximateAge = V("z_age_f") != 0;
            var mail = p.Fields.TryGetValue("z_mail", out var m) ? m.Text.Trim() : "";
            _savedMail = mail == _noMail ? "" : mail;
            Mail = _savedMail;
            // an unknown level shows no choice and is kept (the hook's level 0) unless one's picked
            _savedLevel = p.Level is >= 1 and <= 3 ? p.Level : 0;
            Level = _savedLevel;
            _savedHandle = p.Handle.Trim();
            HandleName = _savedHandle;
        }
        finally { _loadingValues = false; }
        IsLoaded = true;
        Dirty();
    }

    private bool CanSave() => IsLoaded && !Busy && HasChanges && Problem.Length == 0;

    // asks before a rename (old name, new name), true to go ahead. Null skips the question
    internal Func<string, string, bool>? ConfirmRename { get; set; }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (_social.GameRunning)
        {
            Fail(SocialViewModel.GameRunningNote);
            return;
        }
        if (_social.SessionPid is not { } pid)
        {
            Fail("Log in to edit your profile.");
            return;
        }
        if (HandleChanged && ConfirmRename is { } ask && !ask(_savedHandle, HandleName.Trim())) return;
        var fields = ChangedFields();
        _renameAfterSave = HandleChanged;
        if (fields.Count == 0 && Level == _savedLevel)
        {
            StartRename(pid);
            return;
        }
        Start(Job.Save, "Saving your profile...");
        var noMail = fields.Any(f => f.Key == "z_mail" && f.Value == _noMail) ? _noMail : "";
        // level 0 keeps the profile's own, only a level that was picked gets sent
        int level = Level != _savedLevel ? (int)Level : 0;
        if (!SocialSnapshot.SaveMyProfile(pid, _token!, level, fields, noMail)) Fail(WriteFailed);
    }

    private void StartRename(int pid)
    {
        _renameAfterSave = false;
        Start(Job.Rename, "Renaming your handle...");
        if (!SocialSnapshot.RenameHandle(pid, _token!, HandleName.Trim())) Fail(WriteFailed);
    }

    private void OnSnapshot(PolSocialSnapshot snap)
    {
        if (_job == Job.None || _token is null || snap.MeToken != _token) return;
        var state = snap.MeState;
        if (state.StartsWith("failed", StringComparison.Ordinal) || state == "cancelled")
        {
            Fail(FailText(_job, state));
            return;
        }
        switch (_job)
        {
            case Job.Load when state == "ready":
                var p = MyProfile.TryRead(_pid);
                if (p is null || p.Token != _token)
                {
                    Fail("Provenance couldn't read the profile PlayOnline sent.");
                    return;
                }
                EndJob();
                Note = "";
                Fill(p);
                break;
            case Job.Save when state == "saved":
                foreach (var (name, value) in ChangedFields())
                    if (name == "z_mail") _savedMail = Mail.Trim();
                    else if (long.TryParse(value, out var v)) _saved[name] = v;
                _savedLevel = (int)Level;
                if (_renameAfterSave && _social.GameRunning)
                {
                    // Play was pressed while it saved, so the handle stays changed for later
                    _renameAfterSave = false;
                    Done("Your profile is saved. The new handle has to wait till FINAL FANTASY XI is closed.");
                    return;
                }
                if (_renameAfterSave)
                {
                    StartRename(_pid);
                    return;
                }
                Done("Your profile is saved.");
                break;
            case Job.Rename when state == "saved":
                _savedHandle = HandleName.Trim();
                Done("Your profile is saved.");
                break;
        }
    }

    private void Done(string text)
    {
        EndJob();
        NoteIsError = false;
        Dirty();
        Note = text;   // after Dirty, which clears it
    }

    private static string FailText(Job job, string state) => state switch
    {
        "cancelled" => "Logging out stopped the request.",
        "failed:account notice" => "PlayOnline has a notice for this account. Read it in PlayOnline first.",
        "failed:no handle" => "This account doesn't have a PlayOnline handle yet.",
        "failed:no schema" => "Provenance couldn't read the profile layout from your PlayOnline install.",
        "failed:no profile" => "PlayOnline doesn't have a profile for this handle.",
        "failed:not loaded" => "Open the profile again before saving.",
        "failed:nothing to save" => "You haven't changed anything, so there's nothing to save.",
        "failed:bad mail" => "PlayOnline doesn't accept that mail address.",
        "failed:mail too long" => "The mail address is too long.",
        "failed:bad name" => "PlayOnline doesn't accept that handle name. Use English letters, numbers and symbols.",
        "failed:word" => "That handle name has a word in it that PlayOnline doesn't allow. Choose a different name.",
        "failed:-261" => "The entered name could not be registered. Please register with a different name.",
        "failed:reloaded" => "PlayOnline didn't keep the new handle name. Try again later.",
        "failed:busy" => "PlayOnline was busy with other requests. Try again.",
        "failed:mismatch" => "PlayOnline sent a profile that isn't this handle's, so nothing was changed.",
        "failed:write" => "Provenance couldn't keep the profile PlayOnline sent. Try again.",
        "failed:timeout" => job == Job.Load ? "PlayOnline didn't answer." : SaveUnconfirmed,
        _ => job switch
        {
            Job.Load => $"PlayOnline didn't send your profile ({SocialViewModel.FailReason(state)}).",
            Job.Rename => $"PlayOnline didn't rename the handle ({SocialViewModel.FailReason(state)}).",
            _ => $"PlayOnline didn't save your profile ({SocialViewModel.FailReason(state)}).",
        },
    };

    public void Dispose()
    {
        _social.SnapshotApplied -= OnSnapshot;
        _timer.Stop();
        // closed mid-job: an untaken request is withdrawn, one the hook already took finishes there
        if (JobExt is { } ext && _pid != 0) SocialSnapshot.CancelRequest(_pid, ext);
    }
}
