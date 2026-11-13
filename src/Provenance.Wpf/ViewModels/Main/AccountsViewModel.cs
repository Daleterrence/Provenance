using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Credentials;
using Provenance.Common.Login;
using Provenance.Common.Otp;
using Provenance.Common.Settings;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Serilog;

namespace Provenance.Wpf.ViewModels.Main;

public partial class AccountsViewModel : ObservableObject
{
    private readonly IDialogs _dialogs;
    private readonly ICredentialService _credentials;
    private readonly ISettingsService _settings;
    private readonly AvatarService _avatars;

    public AccountsViewModel(IDialogs dialogs, ICredentialService credentials, ISettingsService settings, AvatarService avatars)
    {
        _dialogs = dialogs;
        _credentials = credentials;
        _settings = settings;
        _avatars = avatars;
        Profiles = new ObservableCollection<Account>(_settings.Current.Profiles);
        int needDetails = Profiles.Count(p => !_credentials.HasUsableDetails(p.Id));
        if (needDetails > 0)
            Log.Information("{Count} account(s) need their login details again", needDetails);
        // a fresh settings file's "Default" and a never-logged-in "New account" get a proper name
        int n = 0;
        bool changed = false;
        foreach (var p in Profiles)
        {
            n++;
            if (p.DisplayName is "New account" or "Default")
            {
                p.DisplayName = string.IsNullOrWhiteSpace(p.HandleName) ? $"Account {n}" : p.HandleName!;
                changed = true;
            }
        }
        // an account without a home slot gets one, in list order (the order they were added)
        changed |= HomeSlots.Assign(_settings.Current);
        if (changed) PersistProfileList();   // no settings write on an ordinary start
        avatars.Prewarm(Profiles.Select(p => p.ProfilePictureId));

        var active = Profiles.FirstOrDefault(p => p.Id == _settings.Current.ActiveProfileId) ?? Profiles.FirstOrDefault();
        _selectedProfile = active;
        _autoLogin = _settings.Current.AutoLogin;
        if (active != null) LoadCredentialsForProfile(active);
        _startGameImmediately = _settings.Current.StartGameImmediately;
    }

    public ObservableCollection<Account> Profiles { get; }

    private ObservableCollection<AccountRow>? _rows;

    // made on first read, as each picture's cut out of its sheet on the calling thread
    public ObservableCollection<AccountRow> Rows
    {
        get
        {
            if (_rows is null)
            {
                _rows = new(Profiles.Select(RowOf));
                Profiles.CollectionChanged += OnProfilesChanged;
            }
            return _rows;
        }
    }

    private AccountRow RowOf(Account p) => new(p, p.DisplayName, _avatars.ProfilePicture(p.ProfilePictureId));

    // a row that'd come out the same stays, so an edit doesn't hand the list a new one to redraw
    private void OnProfilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var rows = _rows!;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                rows.Insert(e.NewStartingIndex, RowOf((Account)e.NewItems![0]!));
                break;
            case NotifyCollectionChangedAction.Remove:
                rows.RemoveAt(e.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Replace:
                var row = RowOf((Account)e.NewItems![0]!);
                if (rows[e.NewStartingIndex] != row) rows[e.NewStartingIndex] = row;
                break;
            default:
                rows.Clear();
                foreach (var p in Profiles) rows.Add(RowOf(p));
                break;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile), nameof(ProfileTitle), nameof(ProfilePicture))]
    private Account? _selectedProfile;

    public bool HasProfile => SelectedProfile != null;

    public string ProfileTitle => SelectedProfile?.DisplayName ?? string.Empty;

    public System.Windows.Media.ImageSource? ProfilePicture =>
        _avatars.ProfilePicture(SelectedProfile?.ProfilePictureId ?? 0);

    internal string? SessionProfileId { get; set; }   // can differ from the selection

    internal void LearnIdentity(PolSocialSnapshot snap)
    {
        if (string.IsNullOrWhiteSpace(snap.MyHandle) || SessionProfileId is null) return;
        if (Profiles.FirstOrDefault(p => p.Id == SessionProfileId) is not { } prof) return;
        var key = snap.Handles.FirstOrDefault(h => h.Active)?.Key;
        // no profile picture (-1) says nothing, comparing it anyway would rewrite settings.json every 2s
        if (prof.DisplayName == snap.MyHandle && prof.HandleName == snap.MyHandle &&
            (snap.MyProfilePictureId < 0 || prof.ProfilePictureId == snap.MyProfilePictureId) &&
            (string.IsNullOrEmpty(key) || prof.HandleKey == key)) return;
        prof.DisplayName = snap.MyHandle;
        prof.HandleName = snap.MyHandle;
        if (snap.MyProfilePictureId >= 0) prof.ProfilePictureId = snap.MyProfilePictureId;
        if (!string.IsNullOrEmpty(key)) prof.HandleKey = key;
        PersistProfileList();
        if (prof == SelectedProfile) RefreshProfileDisplay();
        else { var i = Profiles.IndexOf(prof); if (i >= 0) Profiles[i] = prof; }
    }

    partial void OnSelectedProfileChanged(Account? value)
    {
        if (value == null) return;
        _settings.SaveChange(s => s.ActiveProfileId = value.Id);
        LoadCredentialsForProfile(value);
    }

    [ObservableProperty] private bool _useOtp;

    partial void OnUseOtpChanged(bool value)
    {
        if (SelectedProfile is { } p && p.UseOtp != value) { p.UseOtp = value; PersistProfileList(); }
    }

    public bool HasSavedCredentials { get; private set; }

    private void LoadCredentialsForProfile(Account profile)
    {
        UseOtp = profile.UseOtp;
        HasSavedCredentials = _credentials.HasUsableDetails(profile.Id);
    }

    // with slots, the settings lock is held from reading another copy's numbers to saving, so two can't give out one
    private void PersistProfileList(bool slots = false)
    {
        using var gate = slots ? _settings.HoldLock() : null;
        var s = _settings.Current;
        s.Profiles = Profiles.ToList();
        if (slots) HomeSlots.Assign(s, _settings.ReadSaved());
        _settings.Save(s);
    }

    // a login always goes with the account's own home slot, so one without gets it now
    internal int EnsureHomeSlot(Account p)
    {
        if (!HomeSlots.Valid(p.HomeSlot) || Profiles.Any(o => o != p && o.HomeSlot == p.HomeSlot))
            PersistProfileList(slots: true);
        return p.HomeSlot;
    }

    // the IDs are wrapped with the passwords, so this unwraps every saved POL entry
    private Account? ProfileWithPolId(string polId, Account? except = null) =>
        Profiles.FirstOrDefault(p => p != except &&
                                     string.Equals(_credentials.ReadPolId(p.Id), polId, StringComparison.OrdinalIgnoreCase));

    private void RefreshProfileDisplay()
    {
        if (SelectedProfile is not { } p) return;
        var idx = Profiles.IndexOf(p);
        if (idx >= 0) Profiles[idx] = p;
        SelectedProfile = p;
        OnPropertyChanged(nameof(ProfileTitle));
        OnPropertyChanged(nameof(ProfilePicture));
    }

    [RelayCommand]
    private void AddAccount() => TryAddAccount();

    internal bool TryAddAccount()
    {
        var r = _dialogs.AskAccount(null, false, edit: false);
        if (r == null) return false;
        // one POL ID twice would be two cards for one account
        if (ProfileWithPolId(r.PolId) is { } same)
        {
            _dialogs.Info("Account already added",
                $"This PlayOnline ID is already saved as {same.DisplayName}. Press Edit next to it if you want to change its details.");
            SelectedProfile = same;
            return false;
        }
        int n = Profiles.Count(p => p.DisplayName.StartsWith("New account", StringComparison.Ordinal));
        var profile = new Account { DisplayName = n == 0 ? "New account" : $"New account {n + 1}", UseOtp = r.UseOtp,
                                    OtpSource = r.OtpSource, OtpItem = r.OtpItem, OtpAccount = r.OtpAccount };
        _credentials.SavePolAccount(profile.Id, new PolAccount(r.PolId, r.PolPassword));
        try { _credentials.SaveSquareEnixAccount(profile.Id, new SquareEnixAccount(r.SeaId, r.SeaPassword)); }
        catch
        {
            // the account isn't added, so its POL entry mustn't stay behind
            try { _credentials.DeletePolAccount(profile.Id); }
            catch (Exception ex) { Log.Warning(ex, "Could not remove the POL entry of account {Id} after a failed save", profile.Id); }
            throw;
        }
        Profiles.Add(profile);
        PersistProfileList(slots: true);
        SelectedProfile = profile;
        Log.Information("Added account {Id}", profile.Id);
        return true;
    }

    [RelayCommand]
    private void EditAccount(Account? profile) => EditAccount(profile ?? SelectedProfile, requireAll: false);

    internal bool EditAccount(Account? p, bool requireAll, AccountDialogResult? typed = null)
    {
        if (p is null) return false;
        requireAll |= AccountRules.NeedsAllDetails(_credentials.HasUsableDetails(p.Id),
                                      _settings.Current.MultiAccounts.FirstOrDefault(e => e.ProfileId == p.Id)?.Problem);
        var r = _dialogs.AskAccount(p.DisplayName, typed?.UseOtp ?? p.UseOtp, edit: true,
                                    typed?.OtpSource ?? p.OtpSource, typed?.OtpItem ?? p.OtpItem, requireAll: requireAll,
                                    polId: typed?.PolId ?? "", squareEnixId: typed?.SeaId ?? "", otpAccount: typed?.OtpAccount ?? p.OtpAccount);
        if (r == null) return false;
        if (r.PolId.Length > 0 && ProfileWithPolId(r.PolId, except: p) is { } same)
        {
            _dialogs.Info("Account already added",
                $"This PlayOnline ID is already saved as {same.DisplayName}, so nothing was changed.");
            return false;
        }
        var polEdit = AccountRules.SideEditOf(r.PolId, r.PolPassword);
        var squareEnixEdit = AccountRules.SideEditOf(r.SeaId, r.SeaPassword);
        var pol = polEdit == AccountRules.SideEdit.Fill ? _credentials.LoadPolAccount(p.Id) : null;
        var squareEnix = squareEnixEdit == AccountRules.SideEdit.Fill ? _credentials.LoadSquareEnixAccount(p.Id) : null;
        if (AccountRules.FillFailed(polEdit, pol, squareEnixEdit, squareEnix))
            return EditAccount(p, requireAll: true, typed: AccountRules.WithoutPasswords(r));
        // read before anything's saved, so the typed details get compared with the saved ones
        var blocked = _settings.Current.MultiAccounts.FirstOrDefault(e => e.ProfileId == p.Id && e.CredentialsRefused);
        bool cures = blocked is not null && AccountRules.ChangesLogIn(r, _credentials.ReadIds(p.Id), p.UseOtp, p.OtpSource, p.OtpItem, p.OtpAccount);
        if (polEdit != AccountRules.SideEdit.Keep)
            _credentials.SavePolAccount(p.Id, new PolAccount(r.PolId.Length > 0 ? r.PolId : pol!.PolId,
                                                             r.PolPassword.Length > 0 ? r.PolPassword : pol!.Password));
        if (squareEnixEdit != AccountRules.SideEdit.Keep)
            _credentials.SaveSquareEnixAccount(p.Id, new SquareEnixAccount(r.SeaId.Length > 0 ? r.SeaId : squareEnix!.Username,
                                                             r.SeaPassword.Length > 0 ? r.SeaPassword : squareEnix!.Password));
        p.UseOtp = r.UseOtp;
        p.OtpSource = r.OtpSource;
        p.OtpItem = r.OtpItem;
        p.OtpAccount = r.OtpAccount;
        PersistProfileList();
        KeePassXc.ForgetIfUnused(Profiles);
        // without a change that can cure the refusal the refused details are still what'd be sent, so the block stays
        if (blocked is not null && cures)
        {
            var id = blocked.ProfileId;
            _settings.SaveChange(s =>
            {
                if (s.MultiAccounts.FirstOrDefault(e => e.ProfileId == id) is not { } e) return;
                e.CredentialsRefused = false;
                e.Problem = null;
                e.Enabled = true;
            });
        }
        else if (blocked is not null)
            _dialogs.Info("Edit account", NothingChangedText);
        // loading another account's one-time password setting here would write it onto the picked one
        if (p == SelectedProfile)
        {
            LoadCredentialsForProfile(p);
            RefreshProfileDisplay();
        }
        return true;
    }

    internal const string NothingChangedText =
        "You didn't change anything that affects logging in, so this account stays turned off for multi-account login.";

    internal const string RemoveFailedText = "Provenance couldn't remove this account's saved details.";

    [RelayCommand]
    private void RemoveAccount(Account? profile)
    {
        profile ??= SelectedProfile;
        if (profile == null) return;
        if (!_dialogs.Confirm("Remove account",
                $"Remove {profile.DisplayName} and its saved passwords from this PC? Your PlayOnline account itself won't change.",
                "REMOVE", ConfirmStyle.Destructive)) return;
        // the card stays when an entry couldn't go, so no saved password's left without a card that names it
        try { _credentials.DeleteProfile(profile.Id); }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not remove the saved details of account {Id}", profile.Id);
            _dialogs.Error("Remove account", RemoveFailedText, offerLogs: true);
            return;
        }
        // the account list clears the selection when its item goes, so this has to be asked first
        bool wasSelected = profile == SelectedProfile;
        Profiles.Remove(profile);
        PersistProfileList();
        KeePassXc.ForgetIfUnused(Profiles);
        if (wasSelected) SelectedProfile = Profiles.FirstOrDefault();
        Log.Information("Removed account {Id}", profile.Id);
    }

    [ObservableProperty] private bool _autoLogin;

    partial void OnAutoLoginChanged(bool value) => _settings.SaveChange(s => s.AutoLogin = value);

    [ObservableProperty] private bool _startGameImmediately;

    partial void OnStartGameImmediatelyChanged(bool value) => _settings.SaveChange(s => s.StartGameImmediately = value);

    // accounts on one SE ID share an authenticator
    internal string? AskOtp(Account profile, SquareEnixAccount squareEnix) =>
        _dialogs.AskOtp(profile.OtpSource, profile.OtpItem, "sea:" + squareEnix.Username.Trim().ToLowerInvariant(),
                        squareEnix.Username, profile.OtpAccount);

    internal (PolAccount Pol, SquareEnixAccount Sea)? LoadForLogIn(Account profile)
    {
        var details = AccountRules.PrepareLogIn(_credentials, profile.Id);
        if (details.Missing)
        {
            if (!EditAccount(profile, requireAll: true)) return null;
            details = AccountRules.PrepareLogIn(_credentials, profile.Id);
            if (details.Missing)
            {
                _dialogs.Error("Account details incomplete", AccountRules.MissingDetailsText(_credentials.HasEntries(profile.Id)));
                return null;
            }
        }
        if (details.Problem is { } problem)
        {
            _dialogs.Error("Account details incomplete", problem);
            return null;
        }
        return (details.Pol!, details.SquareEnix!);
    }
}
