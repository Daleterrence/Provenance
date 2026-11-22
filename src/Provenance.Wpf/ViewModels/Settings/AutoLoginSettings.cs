using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Login;
using Provenance.Common.Settings;

namespace Provenance.Wpf.ViewModels.Settings;

internal sealed record AutoLoginChoice(string Text, string? Character = null, string? World = null, int? Slot = null);

public sealed partial class AutoLoginRow : ObservableObject
{
    public required string ProfileId { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<string> Choices { get; init; }
    // what each choice stood for when the page was filled, the account's list can change under it
    internal IReadOnlyList<AutoLoginChoice> Picks { get; init; } = [];
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
        TakeList = () => LobbyList.Take(settings);
        Load();
    }

    public ObservableCollection<AutoLoginRow> Rows { get; } = [];

    [ObservableProperty] private bool _acceptLicence;

    partial void OnAcceptLicenceChanged(bool value) { if (!_loading) _settings.SaveChange(s => s.AcceptLicenceAgreement = value); }

    internal Action TakeList { get; set; }

    internal void Load()
    {
        TakeList();
        _loading = true;
        var s = _settings.Current;
        AcceptLicence = s.AcceptLicenceAgreement;
        Rows.Clear();
        foreach (var p in s.Profiles)
        {
            var picks = ChoicesFor(p);
            var row = new AutoLoginRow
            {
                ProfileId = p.Id,
                Name = string.IsNullOrWhiteSpace(p.HandleName) ? p.DisplayName : p.HandleName!,
                Choices = picks.Select(c => c.Text).ToList(),
                Picks = picks,
                HasCharacters = p.Characters.Count > 0,
                Index = IndexOf(p),
            };
            row.Changed = Picked;
            Rows.Add(row);
        }
        _loading = false;
    }

    private static IEnumerable<AutoLoginChoice> Shown(Account p) =>
        p.Characters.Select(c => new AutoLoginChoice(c.World is not null ? $"{c.Name} ({c.World})" : c.Name, c.Name, c.World));

    private static AutoLoginChoice Slot(int n) => new($"Slot {n}", Slot: n);

    private static bool BySlot(Account p) => p.AutoLoginCharacter is null && AutoLogin.IsSlot(p.AutoLoginSlot);

    // slots until the characters are known. One chosen before they were stays on the end, as what's chosen
    private static List<AutoLoginChoice> ChoicesFor(Account p) =>
        p.Characters.Count == 0 ? [new(NoneText), .. Enumerable.Range(1, AutoLogin.Slots).Select(Slot)]
        : BySlot(p) ? [new(NoneText), .. Shown(p), Slot(p.AutoLoginSlot!.Value)]
        : [new(NoneText), .. Shown(p)];

    private static int IndexOf(Account p) =>
        BySlot(p) ? (p.Characters.Count == 0 ? p.AutoLoginSlot!.Value : p.Characters.Count + 1)
        : Math.Max(0, p.Characters.FindIndex(c => AutoLogin.Chosen(p, c) && p.AutoLoginCharacter is not null) + 1);

    private void Picked(AutoLoginRow row)
    {
        if (_loading || row.Index < 0 || row.Index >= row.Picks.Count) return;
        var pick = row.Picks[row.Index];
        _settings.SaveChange(s =>
        {
            if (s.Profiles.FirstOrDefault(p => p.Id == row.ProfileId) is not { } p) return;
            (p.AutoLoginCharacter, p.AutoLoginWorld, p.AutoLoginSlot) = (pick.Character, pick.World, pick.Slot);
        });
    }
}
