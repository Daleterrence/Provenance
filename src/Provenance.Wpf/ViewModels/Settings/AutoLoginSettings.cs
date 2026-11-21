using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Login;
using Provenance.Common.Settings;

namespace Provenance.Wpf.ViewModels.Settings;

public sealed partial class AutoLoginRow : ObservableObject
{
    public required string ProfileId { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<string> Choices { get; init; }
    public required bool HasCharacters { get; init; }
    public string DropdownName => $"Character for {Name}";
    [ObservableProperty] private int _index;
    internal Action<AutoLoginRow>? Changed;

    partial void OnIndexChanged(int value) => Changed?.Invoke(this);
}

public sealed partial class AutoLoginSettings : ObservableObject
{
    public const string IntroText = "Choose a character for each account and Provenance logs in to it once the game starts.";
    public const string NoneText = "Don't log in to a character";
    public const string NoCharactersText = "Its characters appear here after it has logged in once. Until then you can pick by slot on the game's character list.";
    public const string OthersText = "Windower's autoenterkey addon and Ashita's autologin addon are left out whenever Auto-login is doing their job.";

    private readonly ISettingsService _settings;
    private bool _loading;

    internal AutoLoginSettings(ISettingsService settings)
    {
        _settings = settings;
        TakePicks = () => LobbyPicks.Take(settings);
        Load();
    }

    public ObservableCollection<AutoLoginRow> Rows { get; } = [];

    [ObservableProperty] private bool _acceptLicence;

    partial void OnAcceptLicenceChanged(bool value) { if (!_loading) _settings.SaveChange(s => s.AcceptLicenceAgreement = value); }

    internal Action TakePicks { get; set; }

    internal void Load()
    {
        TakePicks();
        _loading = true;
        var s = _settings.Current;
        AcceptLicence = s.AcceptLicenceAgreement;
        Rows.Clear();
        foreach (var p in s.Profiles)
        {
            var row = new AutoLoginRow
            {
                ProfileId = p.Id,
                Name = string.IsNullOrWhiteSpace(p.HandleName) ? p.DisplayName : p.HandleName!,
                Choices = ChoicesFor(p),
                HasCharacters = p.Characters.Count > 0,
                Index = IndexOf(p),
            };
            row.Changed = Picked;
            Rows.Add(row);
        }
        _loading = false;
    }

    private static IEnumerable<string> Shown(Account p) => p.Characters.Select(c => c.World is not null ? $"{c.Name} ({c.World})" : c.Name);

    private static string Slot(int n) => $"Slot {n}";

    private static bool BySlot(Account p) => p.AutoLoginCharacter is null && AutoLogin.IsSlot(p.AutoLoginSlot);

    // slots until the characters are known. One chosen before they were stays on the end, as what's chosen
    private static List<string> ChoicesFor(Account p) =>
        p.Characters.Count == 0 ? [NoneText, .. Enumerable.Range(1, AutoLogin.Slots).Select(Slot)]
        : BySlot(p) ? [NoneText, .. Shown(p), Slot(p.AutoLoginSlot!.Value)]
        : [NoneText, .. Shown(p)];

    private static int IndexOf(Account p) =>
        BySlot(p) ? (p.Characters.Count == 0 ? p.AutoLoginSlot!.Value : p.Characters.Count + 1)
        : Math.Max(0, p.Characters.FindIndex(c => AutoLogin.Chosen(p, c) && p.AutoLoginCharacter is not null) + 1);

    private void Picked(AutoLoginRow row)
    {
        if (_loading) return;
        _settings.SaveChange(s =>
        {
            if (s.Profiles.FirstOrDefault(p => p.Id == row.ProfileId) is not { } p) return;
            if (p.Characters.Count > 0 && row.Index == p.Characters.Count + 1) return;
            var pick = row.Index > 0 && row.Index <= p.Characters.Count ? p.Characters[row.Index - 1] : null;
            p.AutoLoginCharacter = pick?.Name;
            p.AutoLoginWorld = pick?.World;
            p.AutoLoginSlot = p.Characters.Count == 0 && AutoLogin.IsSlot(row.Index) ? row.Index : null;
        });
    }
}
