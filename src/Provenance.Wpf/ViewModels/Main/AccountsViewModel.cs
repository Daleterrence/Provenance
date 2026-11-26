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
        changed |= HomeSlots.Assign(_settings.Current);
        if (changed) PersistProfileList();
        avatars.Prewarm(Profiles.Select(p => p.ProfilePictureId));

        var active = ProfileAtStart(_settings.Current);
        _selectedProfile = active;
        _autoLogin = _settings.Current.AutoLogin;
        if (active != null) LoadCredentialsForProfile(active);
        _startGameImmediately = _settings.Current.StartGameImmediately;
        Profiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanOrder));
        Picks = new(Profiles.Select(p => new AccountPick(p, NameOf(p))));
        Profiles.CollectionChanged += OnPicksChanged;
    }

    public ObservableCollection<Account> Profiles { get; }

    public ObservableCollection<AccountPick> Picks { get; }

    private void OnPicksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                Picks.Insert(e.NewStartingIndex, new AccountPick((Account)e.NewItems![0]!, ""));
                break;
            case NotifyCollectionChangedAction.Remove:
                Picks.RemoveAt(e.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Move:
                Picks.Move(e.OldStartingIndex, e.NewStartingIndex);
                break;
            case NotifyCollectionChangedAction.Replace:
                var account = (Account)e.NewItems![0]!;
                if (Picks[e.NewStartingIndex].Account != account) Picks[e.NewStartingIndex] = new AccountPick(account, "");
                break;
            default:
                Picks.Clear();
                foreach (var p in Profiles) Picks.Add(new AccountPick(p, ""));
                break;
        }
        foreach (var pick in Picks) pick.Name = NameOf(pick.Account);
    }

    internal static Account? ProfileAtStart(LauncherSettings s) =>
        s.Profiles.FirstOrDefault(p => p.Id == s.ActiveProfileId) ?? s.Profiles.FirstOrDefault();

    internal static bool SavedDetails(ICredentialService credentials, Account profile) => credentials.HasUsableDetails(profile.Id);

    private ObservableCollection<AccountRow>? _rows;

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

    internal string NameOf(Account p) => AccountNames.Of(Profiles, p);

    private AccountRow RowOf(Account p) => new(p, NameOf(p), _avatars.ProfilePicture(p.ProfilePictureId));

    // one account added, removed or moved can change another's number
    private void RenameRows()
    {
        var rows = _rows!;
        for (int i = 0; i < rows.Count; i++)
            if (NameOf(rows[i].Account) is var name && name != rows[i].Name) rows[i] = rows[i] with { Name = name };
        OnPropertyChanged(nameof(ProfileTitle));
    }

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
            case NotifyCollectionChangedAction.Move:
                rows.Move(e.OldStartingIndex, e.NewStartingIndex);
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
        RenameRows();
    }

    public bool CanOrder => Profiles.Count > 1;

    internal bool Move(Account p, int by)
    {
        int from = Profiles.IndexOf(p), to = from + by;
        if (from < 0 || to < 0 || to >= Profiles.Count) return false;
        Profiles.Move(from, to);
        PersistProfileList();
        return true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile), nameof(ProfileTitle), nameof(ProfilePicture))]
    private Account? _selectedProfile;

    public bool HasProfile => SelectedProfile != null;

    public string ProfileTitle => SelectedProfile is { } p ? NameOf(p) : string.Empty;

    public System.Windows.Media.ImageSource? ProfilePicture =>
        _avatars.ProfilePicture(SelectedProfile?.ProfilePictureId ?? 0);

    internal string? SessionProfileId { get; set; }

    internal void LearnIdentity(PolSocialSnapshot snap) => LearnIdentity(SessionProfileId, snap);

    internal void LearnIdentity(string? profileId, PolSocialSnapshot snap)
    {
        LearnCharacters(profileId, snap);
        if (string.IsNullOrWhiteSpace(snap.MyHandle) || profileId is null) return;
        if (Profiles.FirstOrDefault(p => p.Id == profileId) is not { } prof) return;
        var key = snap.Handles.FirstOrDefault(h => h.Active)?.Key;
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

    private bool _launchOnly;

    // a command-line login mustn't change which account a normal start logs in
    internal void SelectForLaunch(Account account)
    {
        _launchOnly = true;
        try { SelectedProfile = account; }
        finally { _launchOnly = false; }
    }

    partial void OnSelectedProfileChanged(Account? value)
    {
        if (value == null) return;
        if (!_launchOnly) _settings.SaveChange(s => s.ActiveProfileId = value.Id);
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
        HasSavedCredentials = SavedDetails(_credentials, profile);
    }

    private void PersistProfileList(bool slots = false)
    {
        using var gate = slots ? _settings.HoldLock() : null;
        var s = _settings.Current;
        s.Profiles = Profiles.ToList();
        if (slots) HomeSlots.Assign(s, _settings.ReadSaved());
        _settings.Save(s);
    }

    internal int EnsureHomeSlot(Account p)
    {
        if (!HomeSlots.Valid(p.HomeSlot) || Profiles.Any(o => o != p && o.HomeSlot == p.HomeSlot))
            PersistProfileList(slots: true);
        return p.HomeSlot;
    }

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

    internal AccountCheck? Check { get; set; }

    private Account NewProfile(AccountDialogResult r)
    {
        string masked = AccountRules.MaskedId(r.PolId), name = masked;
        for (int n = 2; Profiles.Any(p => string.Equals(p.DisplayName, name, StringComparison.Ordinal)); n++)
            name = $"{masked} {n}";
        return new Account { DisplayName = name, UseOtp = r.UseOtp, OtpSource = r.OtpSource, OtpItem = r.OtpItem, OtpAccount = r.OtpAccount };
    }

    internal bool TryAddAccount(bool check = true)
    {
        Account? tried = null;
        PolSocialSnapshot? seen = null;
        AccountChecker? checker = check && Check is { } verifier
            ? async (typed, ct) =>
            {
                tried = null;
                seen = null;
                if (ProfileWithPolId(typed.PolId) is not null) return new(true);
                var profile = NewProfile(typed);
                var done = await verifier.RunAsync(profile, typed, ct);
                if (done.Answer.Finish) (tried, seen) = (profile, done.Snapshot);
                return done.Answer;
            }
        : null;
        var r = _dialogs.AskAccount(null, false, edit: false, check: checker);
        if (r == null) return false;
        if (ProfileWithPolId(r.PolId) is { } same)
        {
            _dialogs.Info("Account already added",
                $"This PlayOnline ID is already saved as {NameOf(same)}. Press Edit next to it if you want to change its details.");
            SelectedProfile = same;
            return false;
        }
        var profile = tried ?? NewProfile(r);
        _credentials.SavePolAccount(profile.Id, new PolAccount(r.PolId, r.PolPassword));
        try { _credentials.SaveSquareEnixAccount(profile.Id, new SquareEnixAccount(r.SeaId, r.SeaPassword)); }
        catch
        {
            try { _credentials.DeletePolAccount(profile.Id); }
            catch (Exception ex) { Log.Warning(ex, "Could not remove the PlayOnline login details of account {Id} after a failed save", profile.Id); }
            throw;
        }
        Profiles.Add(profile);
        PersistProfileList(slots: true);
        SelectedProfile = profile;
        if (seen is { } snap) LearnIdentity(profile.Id, snap);
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
        var r = _dialogs.AskAccount(NameOf(p), typed?.UseOtp ?? p.UseOtp, edit: true,
                                    typed?.OtpSource ?? p.OtpSource, typed?.OtpItem ?? p.OtpItem, requireAll: requireAll,
                                    polId: typed?.PolId ?? "", squareEnixId: typed?.SeaId ?? "", otpAccount: typed?.OtpAccount ?? p.OtpAccount);
        if (r == null) return false;
        if (r.PolId.Length > 0 && ProfileWithPolId(r.PolId, except: p) is { } same)
        {
            _dialogs.Info("Account already added",
                $"This PlayOnline ID is already saved as {NameOf(same)}, so nothing was changed.");
            return false;
        }
        var polEdit = AccountRules.SideEditOf(r.PolId, r.PolPassword);
        var squareEnixEdit = AccountRules.SideEditOf(r.SeaId, r.SeaPassword);
        var pol = polEdit == AccountRules.SideEdit.Fill ? _credentials.LoadPolAccount(p.Id) : null;
        var squareEnix = squareEnixEdit == AccountRules.SideEdit.Fill ? _credentials.LoadSquareEnixAccount(p.Id) : null;
        if (AccountRules.FillFailed(polEdit, pol, squareEnixEdit, squareEnix))
            return EditAccount(p, requireAll: true, typed: AccountRules.WithoutPasswords(r));
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

    internal string? MessagesRoot { get; set; }

    internal Action<string>? MessagesEnding { get; set; }

    [RelayCommand]
    private void RemoveAccount(Account? profile)
    {
        profile ??= SelectedProfile;
        if (profile == null) return;
        if (!_dialogs.Confirm("Remove account",
                $"Remove {NameOf(profile)}, its saved passwords and its saved messages from this PC? Your PlayOnline account itself won't change.",
                "REMOVE", ConfirmStyle.Destructive)) return;
        try { _credentials.DeleteProfile(profile.Id); }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not remove the saved details of account {Id}", profile.Id);
            _dialogs.Error("Remove account", RemoveFailedText, offerLogs: true);
            return;
        }
        MessagesEnding?.Invoke(profile.Id);
        AccountMessages.Delete(profile.Id, MessagesRoot);
        // the account list clears the selection when its item goes, so this has to be asked first
        bool wasSelected = profile == SelectedProfile;
        Profiles.Remove(profile);
        _settings.Current.MultiAccounts.RemoveAll(e => e.ProfileId == profile.Id);
        PersistProfileList();
        KeePassXc.ForgetIfUnused(Profiles);
        if (wasSelected) SelectedProfile = Profiles.FirstOrDefault();
        Log.Information("Removed account {Id}", profile.Id);
    }

    [ObservableProperty] private bool _autoLogin;

    partial void OnAutoLoginChanged(bool value) => _settings.SaveChange(s => s.AutoLogin = value);

    [ObservableProperty] private bool _startGameImmediately;

    partial void OnStartGameImmediatelyChanged(bool value) => _settings.SaveChange(s => s.StartGameImmediately = value);

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
