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
    public bool HasCharacters => Choices.Count > 1;
    public string DropdownName => $"Character for {Name}";
    [ObservableProperty] private int _index;
    internal Action<AutoLoginRow>? Changed;

    partial void OnIndexChanged(int value) => Changed?.Invoke(this);
}

public sealed partial class AutoLoginSettings : ObservableObject
{
    public const string IntroText = "Choose a character for each account and Provenance logs in to it once the game starts.";
    public const string NoneText = "Don't log in to a character";
    public const string NoCharactersText = "Its characters appear here after it has logged in once.";
    public const string OthersText = "Windower's autoenterkey addon and Ashita's autologin addon are left out whenever Auto-login is doing their job.";

    private readonly ISettingsService _settings;
    private bool _loading;

    internal AutoLoginSettings(ISettingsService settings)
    {
        _settings = settings;
        Load();
    }

    public ObservableCollection<AutoLoginRow> Rows { get; } = [];

    [ObservableProperty] private bool _acceptLicence;

    partial void OnAcceptLicenceChanged(bool value) { if (!_loading) _settings.SaveChange(s => s.AcceptLicenceAgreement = value); }

    internal void Load()
    {
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
                Choices = [NoneText, .. Shown(p)],
                Index = Math.Max(0, p.Characters.FindIndex(c => AutoLogin.Chosen(p, c) && p.AutoLoginCharacter is not null) + 1),
            };
            row.Changed = Picked;
            Rows.Add(row);
        }
        _loading = false;
    }

    private static IEnumerable<string> Shown(Account p) => p.Characters.Select(c => c.World is not null ? $"{c.Name} ({c.World})" : c.Name);

    private void Picked(AutoLoginRow row)
    {
        if (_loading) return;
        _settings.SaveChange(s =>
        {
            if (s.Profiles.FirstOrDefault(p => p.Id == row.ProfileId) is not { } p) return;
            var pick = row.Index > 0 && row.Index <= p.Characters.Count ? p.Characters[row.Index - 1] : null;
            p.AutoLoginCharacter = pick?.Name;
            p.AutoLoginWorld = pick?.World;
        });
    }
}
