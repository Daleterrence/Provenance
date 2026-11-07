using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Hook;
using Provenance.Common.Login;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.ViewModels.Social;

// only changed fields get sent, and a rename goes after the save
public sealed partial class EditProfileViewModel : ObservableObject, IDisposable
{
    private enum Job { None, Load, Save, Rename, LoadXi }

    private readonly SocialViewModel _social;
    private readonly IDialogs _dialogs;
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Job _job;
    private string? _token;
    private int _pid;
    internal DateTime _at;
    private string _noMail = "No Mail address";
    private string _savedHandle = "";
    private string _loadedKey = "";
    private string _savedMail = "";
    private PrivacyLevel _savedLevel;
    private readonly Dictionary<string, long> _saved = new();
    private bool _renameAfterSave;
    private bool _loadingValues;
    private bool _handleChanges;
    private ProfileCard? _xiCard;
    private readonly ProfileCard _handleCard;

    public EditProfileViewModel(SocialViewModel social, IDialogs dialogs)
    {
        _social = social;
        _dialogs = dialogs;
        _social.SnapshotApplied += OnSnapshot;
        _timer.Tick += (_, _) => CheckTimeout();
        NotSet = social.ViewerText(0x4a81, "Not set");
        _handleCard = new ProfileCard(null, HelpFor)
        {
            Title = social.AccountName,
            Subtitle = social.ViewerText(0x4abe, "Handle Profile"),
            Picture = social.Avatar,
            IsSelected = true,
        };
        SelectedCard = _handleCard;
        Cards.Add(_handleCard);
        _social.PropertyChanged += OnSocialChanged;
        Note.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FooterText));
            OnPropertyChanged(nameof(FooterIsError));
            OnPropertyChanged(nameof(CanRetryLoad));
        };
    }

    private void OnSocialChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SocialViewModel.Avatar)) _handleCard.Picture = _social.Avatar;
        if (e.PropertyName == nameof(SocialViewModel.AccountName) && !IsLoaded) _handleCard.Title = _social.AccountName;
    }

    public SocialViewModel Social => _social;

    public ObservableCollection<ProfileCard> Cards { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHandleCard), nameof(IsXiCard), nameof(CanRetryLoad))]
    private ProfileCard _selectedCard = null!;

    public bool IsHandleCard => SelectedCard.IsHandle;
    public bool IsXiCard => !SelectedCard.IsHandle;
    public bool CanSelectCard => !Busy;

    [ObservableProperty] private string _loadingText = "Loading your profile...";

    // edits stay with each card, so picking another one never loses any
    [RelayCommand]
    private void SelectCard(ProfileCard? card)
    {
        if (card is null || Busy || card == SelectedCard) return;
        SelectedCard.IsSelected = false;
        card.IsSelected = true;
        SelectedCard = card;
        if (Note.IsError) Note.Clear();
        if (!card.IsHandle && !card.Loaded) LoadXi(card);
    }

    public string NotSet { get; }
    public IReadOnlyList<Choice<PrivacyLevel>> VisibilityChoices { get; } =
        [new(PrivacyLevel.Everyone, "Everyone on PlayOnline"), new(PrivacyLevel.FriendsOnly, "Friends only"),
         new(PrivacyLevel.Private, "Nobody (private)")];

    [ObservableProperty] private IReadOnlyList<Choice<long>> _sexChoices = [];
    [ObservableProperty] private IReadOnlyList<Choice<long>> _jobChoices = [];
    [ObservableProperty] private IReadOnlyList<Choice<long>> _languageChoices = [];
    [ObservableProperty] private CascadePick? _location;
    [ObservableProperty] private CascadePick? _interest1;
    [ObservableProperty] private CascadePick? _interest2;
    [ObservableProperty] private CascadePick? _interest3;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditFields), nameof(CanRetryLoad))] private bool _isLoaded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditFields), nameof(FooterText), nameof(FooterIsError), nameof(CanRetryLoad),
                              nameof(CanSelectCard))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _busy;
    public StatusLine Note { get; } = new();

    // puts the window's loading overlay up. Only the load gets one, a save leaves the form in view
    [ObservableProperty] private bool _loadingProfile;

    public string FooterText => !Busy && Problem.Length > 0 ? Problem : Note.Text;
    public bool FooterIsError => !Busy && Problem.Length > 0 || Note.IsError;
    public bool CanEditFields => IsLoaded && !Busy;
    public bool CanRetryLoad => !Busy && Note.IsError && (IsHandleCard ? !IsLoaded : !SelectedCard.Loaded);

    [RelayCommand]
    private void RetryLoad()
    {
        if (IsHandleCard) Load();
        else LoadXi(SelectedCard);
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(VisibilityHelp))]
    private PrivacyLevel _level;   // Keep if it loaded as none of the others

    // the viewer's own help for each choice, the same window sets a character's
    public string VisibilityHelp => HelpFor(Level);

    private string HelpFor(PrivacyLevel level) => level switch
    {
        PrivacyLevel.Everyone => _social.ViewerText(0x2c5f, "Show profile to all PlayOnline members."),
        PrivacyLevel.FriendsOnly => _social.ViewerText(0x2c60, "Show profile to friends and group friends only."),
        PrivacyLevel.Private => _social.ViewerText(0x2c61, "Hide profile from all PlayOnline members."),
        _ => "",
    };
    [ObservableProperty] private string _handleName = "";
    [ObservableProperty] private string _mail = "";
    [ObservableProperty] private string _ageText = "";
    [ObservableProperty] private bool _approximateAge;
    [ObservableProperty] private long _sex;
    [ObservableProperty] private long _job1;
    [ObservableProperty] private long _lang0;
    [ObservableProperty] private long _lang1;
    [ObservableProperty] private long _lang2;

    partial void OnLevelChanged(PrivacyLevel value) => Dirty();
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
        _handleChanges = ChangedFields().Count > 0 || Level != _savedLevel || HandleChanged;
        _handleCard.HasChanges = _handleChanges;
        HasChanges = _handleChanges;
        SaveCommand.NotifyCanExecuteChanged();
        if (!Busy && !Note.IsError) Note.Clear();
    }

    private bool HandleChanged => HandleName.Trim() != _savedHandle;

    // the viewer's checks that work here, the hook and the server check again anyway
    private string Check()
    {
        var name = HandleName.Trim();
        if (name.Length == 0) return "Enter a handle name.";
        if (name.Length > PolLimits.HandleName) return $"A handle name can be {PolLimits.HandleName} characters at most.";
        if (name.Any(c => c < 0x20 || c > 0x7e)) return "A handle name can only use English letters, numbers and symbols.";
        var age = AgeText.Trim();
        if (age.Length > 0 && !(age.All(char.IsAsciiDigit) && age.Length <= 3)) return "Age is a number from 1 to 999.";
        if (Mail.Trim().Length > PolLimits.Mail) return HookFailures.ProfileEditor.MailTooLong;
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
        if (_social.GameRunning || _social.HandleSwitching)
        {
            Fail(_social.GameRunning ? SocialViewModel.GameRunningNote : SocialViewModel.SwitchingNote);
            return;
        }
        Start(Job.Load);
        if (!SocialRequests.RequestMyProfile(pid, _token!)) Fail(HookFailures.RequestWriteFailed);
    }

    private void LoadXi(ProfileCard card)
    {
        if (_social.SessionPid is not { } pid)
        {
            Fail("Log in to edit your profile.");
            return;
        }
        if (_social.GameRunning || _social.HandleSwitching)
        {
            Fail(_social.GameRunning ? SocialViewModel.GameRunningNote : SocialViewModel.SwitchingNote);
            return;
        }
        _xiCard = card;
        LoadingText = $"Loading {card.Title}'s profile...";
        Start(Job.LoadXi);
        if (!SocialRequests.RequestXiProfile(pid, _token!, _loadedKey, card.Key)) Fail(HookFailures.RequestWriteFailed);
    }

    private void Start(Job job, string note = "")
    {
        _job = job;
        _token = HookFiles.NewToken();
        _pid = _social.SessionPid ?? 0;
        _at = DateTime.UtcNow;
        if (job == Job.Load) LoadingText = "Loading your profile...";
        LoadingProfile = job is Job.Load or Job.LoadXi;
        Busy = true;
        Note.Show(note);
        _timer.Start();
    }


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
        Note.Fail(text);
    }

    private string? JobExt => _job switch
    {
        Job.Load => HookContract.Requests.MyProfile,
        Job.Save => HookContract.Requests.SaveProfile,
        Job.Rename => HookContract.Requests.Rename,
        Job.LoadXi => HookContract.Requests.XiProfile,
        _ => null,
    };

    // withdraws a request the hook hasn't taken yet, so it never runs later
    private void Abandon(string text)
    {
        if (JobExt is { } ext && _pid != 0) HookFiles.Discard(_pid, ext);
        Fail(text);
    }

    // the hook gives up waiting for a slot at 120 secs and on a rename at 240, and polcore fails a job at 100, this
    // catches one gone quiet
    internal void CheckTimeout()
    {
        if (_job == Job.None)
        {
            _timer.Stop();
            return;
        }
        if (_social.SessionPid != _pid)
        {
            Abandon(HookFailures.ProfileEditor.SessionEnded);
            return;
        }
        if (DateTime.UtcNow - _at > TimeSpan.FromSeconds(_job == Job.Rename ? 260 : 250))
            Abandon(_job is Job.Load or Job.LoadXi ? HookFailures.ProfileEditor.LoadNoAnswer : HookFailures.ProfileEditor.SaveUnconfirmed);
    }

    private void Fill(MyProfile p)
    {
        _loadedKey = p.Key;
        _loadingValues = true;
        try
        {
            _noMail = _social.ViewerText(0x4a7f, "No Mail address");
            IReadOnlyList<MyProfileChoice> T(string n) => p.Tables.TryGetValue(n, out var t) ? t : [];
            List<Choice<long>> One(string n) =>
                [new(0, NotSet), .. T(n).Select(r => new Choice<long>(r.Code, r.Parts.LastOrDefault()?.Trim() ?? ""))];
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
            long Keep(IReadOnlyList<Choice<long>> list, long v, Action<IReadOnlyList<Choice<long>>> set)
            {
                if (v != 0 && list.All(c => c.Code != v)) set(list.Append(new Choice<long>(v, NotSet)).ToList());
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
            _savedLevel = p.Level is >= PrivacyLevel.Private and <= PrivacyLevel.Everyone ? p.Level : PrivacyLevel.Keep;
            Level = _savedLevel;
            _savedHandle = p.Handle.Trim();
            HandleName = _savedHandle;
            _handleCard.Title = _savedHandle;
            FillCards(p.Characters);
        }
        finally { _loadingValues = false; }
        IsLoaded = true;
        Dirty();
    }

    // a reload starts the characters over, they're read again when picked
    private void FillCards(IReadOnlyList<MyProfileCharacter> characters)
    {
        foreach (var c in Cards.Where(c => !c.IsHandle).ToList())
        {
            c.PropertyChanged -= OnCardChanged;
            Cards.Remove(c);
        }
        if (!SelectedCard.IsHandle)
        {
            SelectedCard.IsSelected = false;
            _handleCard.IsSelected = true;
            SelectedCard = _handleCard;
        }
        foreach (var c in characters)
        {
            var card = new ProfileCard(c, HelpFor)
            {
                Title = c.Name.Length > 0 ? c.Name : "Content ID",
                Subtitle = c.WorldName ?? "",
            };
            card.PropertyChanged += OnCardChanged;
            Cards.Add(card);
        }
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileCard.Loaded) && sender == SelectedCard) OnPropertyChanged(nameof(CanRetryLoad));
    }

    private bool CanSave() => IsLoaded && !Busy && HasChanges && Problem.Length == 0;

    // a load has nothing of theirs in it yet, closing just withdraws the request
    private bool Saving => Busy && !LoadingProfile;

    internal bool HasUnsavedWork => Saving || HasChanges;

    internal bool ConfirmClose()
    {
        if (Saving)
            return _dialogs.Confirm("Saving your profile",
                "Your changes are on their way to PlayOnline. Close anyway? The save might not finish.", "CLOSE", ConfirmStyle.Destructive);
        return !HasChanges || _dialogs.Confirm("Unsaved profile changes", "Close without saving your changes?", "DISCARD", ConfirmStyle.Destructive);
    }

    internal bool ConfirmDrop(string action) =>
        !HasUnsavedWork || _dialogs.Confirm("Unsaved profile changes",
            $"You haven't saved your profile changes. {action} anyway? They'll be lost.", action.ToUpperInvariant(), ConfirmStyle.Destructive);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (_social.GameRunning || _social.HandleSwitching)
        {
            Fail(_social.GameRunning ? SocialViewModel.GameRunningNote : SocialViewModel.SwitchingNote);
            return;
        }
        if (_social.SessionPid is not { } pid)
        {
            Fail("Log in to edit your profile.");
            return;
        }
        if (_handleChanges && HandleChanged && !_dialogs.Confirm("Rename your handle",
                $"Friends will see {HandleName.Trim()} instead of {_savedHandle} the next time their friend list loads. You can rename it again later.", "RENAME"))
            return;
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
        var level = Level != _savedLevel ? Level : PrivacyLevel.Keep;
        if (!SocialRequests.SaveMyProfile(pid, _token!, level, fields, noMail)) Fail(HookFailures.RequestWriteFailed);
    }

    private void StartRename(int pid)
    {
        _renameAfterSave = false;
        Start(Job.Rename, "Renaming your handle...");
        if (!SocialRequests.RenameHandle(pid, _token!, HandleName.Trim(), _loadedKey)) Fail(HookFailures.RequestWriteFailed);
    }

    private void OnSnapshot(PolSocialSnapshot snap)
    {
        bool xi = _job == Job.LoadXi;
        var hook = xi ? snap.XiJob : snap.MeJob;
        // waiting its turn behind another profile server job in the hook, so its clock hasn't started
        if (_job != Job.None && _token is not null && hook.Token != _token && snap.ProfileServerBusy) _at = DateTime.UtcNow;
        if (_job == Job.None || _token is null || hook.Token != _token) return;
        var state = hook.State;
        if (state.StartsWith("failed", StringComparison.Ordinal) || state == "cancelled")
        {
            var job = _job;
            var text = FailText(job, state);
            Fail(text);
            if (job is Job.Save or Job.Rename && state != "cancelled") _dialogs.Error("Not saved", text);
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
                Note.Clear();
                Fill(p);
                break;
            case Job.Save when state == "saved":
                foreach (var (name, value) in ChangedFields())
                    if (name == "z_mail") _savedMail = Mail.Trim();
                    else if (long.TryParse(value, out var v)) _saved[name] = v;
                _savedLevel = Level;
                if (_renameAfterSave && _social.GameRunning)
                {
                    // Play was pressed while it saved, so the handle stays changed for later
                    _renameAfterSave = false;
                    Done("Your profile is saved. Your handle wasn't renamed because FINAL FANTASY XI started.");
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
                _handleCard.Title = _savedHandle;
                Done("Your profile is saved.");
                break;
            case Job.LoadXi when state == "ready":
                if (_xiCard is not { } card || snap.XiKey != card.Key)
                {
                    Fail("Provenance couldn't read the profile PlayOnline sent.");
                    return;
                }
                EndJob();
                Note.Clear();
                card.Loaded = true;
                card.Level = snap.XiLevel is >= PrivacyLevel.Private and <= PrivacyLevel.Everyone ? snap.XiLevel : PrivacyLevel.Keep;
                if (card.Character is { Name.Length: 0 } && snap.XiName.Trim().Length > 0) card.Title = snap.XiName.Trim();
                break;
        }
    }

    private void Done(string text)
    {
        EndJob();
        _xiCard = null;
        Dirty();
        Note.Show(text);   // after Dirty, which clears it
    }

    private static string FailText(Job job, string state) => state switch
    {
        "cancelled" => HookFailures.ProfileEditor.Cancelled,
        "failed:handle changing" => job == Job.Load ? HookFailures.ProfileEditor.LoadHandleChanging
                                                  : HookFailures.ProfileEditor.SaveHandleChanging,
        "failed:no schema" => HookFailures.ProfileEditor.NoSchema,
        "failed:no profile" => HookFailures.ProfileEditor.NoProfile,
        "failed:not loaded" => HookFailures.ProfileEditor.NotLoaded,
        "failed:nothing to save" => HookFailures.ProfileEditor.NothingToSave,
        "failed:bad mail" => HookFailures.ProfileEditor.BadMail,
        "failed:mail too long" => HookFailures.ProfileEditor.MailTooLong,
        "failed:bad name" => HookFailures.ProfileEditor.BadName,
        "failed:word" => HookFailures.ProfileEditor.BadWord,
        "failed:-261" or "failed:-5429" => HookFailures.ProfileEditor.NameRejected,
        "failed:reloaded" => HookFailures.ProfileEditor.NameNotKept,
        "failed:busy" => HookFailures.Busy,
        "failed:mismatch" => job == Job.LoadXi ? HookFailures.ProfileEditor.MismatchCharacter
                                              : HookFailures.ProfileEditor.MismatchHandle,
        "failed:no character" => HookFailures.ProfileEditor.CharacterGone,
        "failed:write" => HookFailures.ProfileEditor.WriteFailed,
        "failed:timeout" => job is Job.Load or Job.LoadXi ? HookFailures.ProfileEditor.LoadNoAnswer : HookFailures.ProfileEditor.SaveUnconfirmed,
        _ => job switch
        {
            Job.Load => HookFailures.ProfileEditor.LoadFailed(Why(state)),
            Job.LoadXi => HookFailures.ProfileEditor.LoadCharacterFailed(Why(state)),
            Job.Rename => HookFailures.ProfileEditor.RenameFailed(Why(state)),
            _ => HookFailures.ProfileEditor.SaveFailed(Why(state)),
        },
    };

    // a server code gets the viewer's own words for it. 5440's are about group rights, but it's the server's
    // general "not authorised", so it gets plain ones
    private static string Why(string state)
    {
        var reason = SocialRequests.FailReason(state);
        if (!int.TryParse(reason, out var n) || n >= 0) return $" ({reason}).";
        int code = -n;
        var text = code == 5440 ? HookFailures.ProfileEditor.NotAllowed : PolErrors.Text(code);
        return text is null || text.Contains("%d") ? $" (POL-{code:0000})." : $" (POL-{code:0000}). {text}";
    }

    public void Dispose()
    {
        _social.SnapshotApplied -= OnSnapshot;
        _social.PropertyChanged -= OnSocialChanged;
        _timer.Stop();
        // closed mid-job: an untaken request is withdrawn, one the hook already took finishes there
        if (JobExt is { } ext && _pid != 0) HookFiles.Discard(_pid, ext);
    }
}
