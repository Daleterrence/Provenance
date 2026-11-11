using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Login;
using Provenance.Common.Settings;

namespace Provenance.Wpf.ViewModels.Settings;

// The multi-account page. The first profile choice is none, which an account that's turned on can't start with
public partial class MultiAccountSettings : ObservableObject
{
    private readonly HookSettings _hook;
    private readonly ISettingsService _settings;
    private readonly SettingsContext _context;
    private bool _loadingMulti;

    // each account's stored Enabled as last loaded or saved. One that's gone off since got turned off by a run
    private Dictionary<string, bool> _multiEnabledAsKnown = new();

    internal MultiAccountSettings(HookSettings hook, ISettingsService settings, SettingsContext context)
    {
        _hook = hook;
        _settings = settings;
        _context = context;
        hook.ListsRefreshed += () => RefreshMultiChoices();
        hook.PlatformSwitched += RefreshMultiChoices;
        hook.AccountsReload += Load;
    }

    public string ProfileDefaultChoice => _hook.IsAshita ? "Select Ashita boot configuration..." : "Select Windower profile...";
    public ObservableCollection<MultiAccountRow> MultiAccountRows { get; } = new();
    public ObservableCollection<string> MultiProfileChoices { get; } = new();
    public bool MultiHasThirdPartyHook => _hook.Hook != ThirdPartyHook.None;
    public string MultiProfileLabel => _hook.IsAshita ? "Ashita boot configuration" : "Windower profile";

    // read-only while a run goes, it saves refused and blocked accounts as they happen and a save from here
    // mustn't undo that
    public bool MultiRunGoing => _context.MultiRunning();
    public bool MultiEditable => MultiHasThirdPartyHook && !MultiRunGoing;

    [ObservableProperty] private bool _multiContinueOnError;
    [ObservableProperty] private bool _multiCloseWhenFinished;

    partial void OnMultiContinueOnErrorChanged(bool value) => SaveUnlessLoading(s => s.MultiContinueOnError = value);
    partial void OnMultiCloseWhenFinishedChanged(bool value) =>
        SaveUnlessLoading(s => s.MultiCloseWhenFinished = value);
    private void SaveUnlessLoading(Action<LauncherSettings> change) { if (!_loadingMulti) _settings.SaveChange(change); }

    public void RunChanged()
    {
        Load();
        OnPropertyChanged(nameof(MultiRunGoing));
        OnPropertyChanged(nameof(MultiEditable));
    }

    private void RememberMulti(IEnumerable<MultiAccountEntry> stored) =>
        _multiEnabledAsKnown = stored.GroupBy(e => e.ProfileId).ToDictionary(g => g.Key, g => g.First().Enabled);

    internal void Load()
    {
        _loadingMulti = true;
        MultiAccountRows.Clear();
        var s = _settings.Current;
        RememberMulti(s.MultiAccounts);
        MultiContinueOnError = s.MultiContinueOnError;
        MultiCloseWhenFinished = s.MultiCloseWhenFinished;
        var byId = s.Profiles.ToDictionary(p => p.Id);
        var ids = s.MultiAccounts.Select(e => e.ProfileId).Where(byId.ContainsKey).Distinct()
                   .Concat(s.Profiles.Select(p => p.Id)).Distinct().ToList();
        foreach (var id in ids)
        {
            var entry = s.MultiAccounts.FirstOrDefault(e => e.ProfileId == id);
            var p = byId[id];
            var row = new MultiAccountRow
            {
                ProfileId = id,
                Name = string.IsNullOrWhiteSpace(p.HandleName) ? p.DisplayName : p.HandleName!,
                Enabled = entry?.Enabled ?? false,
                Problem = HookExitText.Current(entry?.Problem),
                CredentialsRefused = entry?.CredentialsRefused ?? false,
                ThirdPartyHookProfile = ToChoice(entry?.ThirdPartyHookProfile),
            };
            row.Changed = () => { SaveMulti(); NoteUnpicked(); };
            MultiAccountRows.Add(row);
        }
        _loadingMulti = false;
        RefreshMultiChoices();
    }

    private ObservableCollection<HookProfileItem>? ChoiceItems =>
        _hook.Hook switch { ThirdPartyHook.Ashita => _hook.AshitaBootConfigs, ThirdPartyHook.Windower => _hook.WindowerProfiles, _ => null };

    private string ToChoice(string? stored) =>
        stored is null ? ProfileDefaultChoice
        : ChoiceItems is { } items ? HookSettings.Item(items, stored)?.Display ?? stored
        : stored;

    private string? FromChoice(string choice) =>
        choice == ProfileDefaultChoice ? null
        : ChoiceItems?.FirstOrDefault(i => i.Display == choice)?.Name ?? choice;

    // a Windower profile means nothing to Ashita and vice versa, so a platform switch clears every stored one
    private void RefreshMultiChoices(bool platformChanged = false)
    {
        _loadingMulti = true;
        var keep = MultiAccountRows.Select(r => r.ThirdPartyHookProfile).ToList();
        MultiProfileChoices.Clear();
        MultiProfileChoices.Add(ProfileDefaultChoice);
        foreach (var c in ChoiceItems ?? Enumerable.Empty<HookProfileItem>())
            MultiProfileChoices.Add(c.Display);
        for (int i = 0; i < MultiAccountRows.Count; i++)
            MultiAccountRows[i].ThirdPartyHookProfile = !platformChanged && MultiProfileChoices.Contains(keep[i]) ? keep[i] : ProfileDefaultChoice;
        _loadingMulti = false;
        if (platformChanged) ClearMultiProfiles();
        OnPropertyChanged(nameof(ProfileDefaultChoice));
        OnPropertyChanged(nameof(MultiHasThirdPartyHook));
        OnPropertyChanged(nameof(MultiEditable));
        OnPropertyChanged(nameof(MultiProfileLabel));
        NoteUnpicked();
    }

    private void NoteUnpicked()
    {
        foreach (var r in MultiAccountRows)
            r.ProfileNeeded = MultiHasThirdPartyHook && r.Enabled && r.ThirdPartyHookProfile == ProfileDefaultChoice
                ? $"Choose the {MultiProfileLabel} {r.Name} starts with." : null;
    }

    // safe mid-run too: the run read its profiles at the start, and only the profile changes
    private void ClearMultiProfiles()
    {
        var withProfile = _settings.Current.MultiAccounts.Where(e => e.ThirdPartyHookProfile is not null).ToList();
        if (withProfile.Count == 0) return;
        _settings.SaveChange(s =>
        {
            foreach (var e in s.MultiAccounts.Where(e => e.ThirdPartyHookProfile is not null)) e.ThirdPartyHookProfile = null;
        });
    }

    private void SaveMulti()
    {
        if (_loadingMulti || MultiRunGoing) return;
        // read the stored list again, a run may have saved a failure since the rows were loaded
        var stored = _settings.Current.MultiAccounts;
        var saved = new List<MultiAccountEntry>();
        _loadingMulti = true;   // the rows brought in line below mustn't save again
        try
        {
            foreach (var r in MultiAccountRows)
            {
                var entry = new MultiAccountEntry { ProfileId = r.ProfileId, Enabled = r.Enabled, ThirdPartyHookProfile = FromChoice(r.ThirdPartyHookProfile), Problem = r.Problem };
                // a refused password, or one a run turned off since the load: the stored state wins, so no edit
                // here turns it back on by mistake
                if (stored.FirstOrDefault(e => e.ProfileId == r.ProfileId) is { } now &&
                    (now.CredentialsRefused || (!now.Enabled && _multiEnabledAsKnown.GetValueOrDefault(r.ProfileId))))
                {
                    entry.Enabled = false;
                    entry.Problem = HookExitText.Current(now.Problem);
                    entry.CredentialsRefused = now.CredentialsRefused;
                    r.Enabled = false;
                    r.Problem = entry.Problem;
                    r.CredentialsRefused = now.CredentialsRefused;
                }
                saved.Add(entry);
            }
        }
        finally { _loadingMulti = false; }
        _settings.Current.MultiAccounts = saved;
        _settings.Save(_settings.Current);
        RememberMulti(saved);
    }

    [RelayCommand] private void MoveMultiUp(MultiAccountRow? row) => MoveMulti(row, -1);
    [RelayCommand] private void MoveMultiDown(MultiAccountRow? row) => MoveMulti(row, +1);

    private void MoveMulti(MultiAccountRow? row, int by)
    {
        if (row is null) return;
        int i = MultiAccountRows.IndexOf(row), j = i + by;
        if (i < 0 || j < 0 || j >= MultiAccountRows.Count) return;
        MultiAccountRows.Move(i, j);
        SaveMulti();
    }
}
