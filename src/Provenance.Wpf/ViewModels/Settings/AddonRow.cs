using CommunityToolkit.Mvvm.ComponentModel;

namespace Provenance.Wpf.ViewModels.Settings;

public sealed partial class AddonRow : ObservableObject
{
    private readonly AddonsPluginsViewModel _owner;
    private bool _on;

    internal AddonRow(AddonsPluginsViewModel owner, RowData d)
    {
        _owner = owner;
        Kind = d.Kind;
        Key = d.Listing.Key;
        Update(d);
    }

    public AddonKind Kind { get; }
    public string Key { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _details;
    [ObservableProperty] private string? _description;
    [ObservableProperty] private IReadOnlyList<AddonBadge> _badges = [];
    [ObservableProperty] private bool _hasSwitch;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SwitchEnabled))] private bool _canChange;
    [ObservableProperty] private bool _canInstall;
    [ObservableProperty] private bool _incompatible;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SwitchEnabled))] private bool _installing;
    [ObservableProperty] private bool _visible = true;
    [ObservableProperty] private string? _verifiedTip;
    [ObservableProperty] private string? _team;
    [ObservableProperty] private string? _location;
    private string? _author;

    public bool SwitchEnabled => CanChange && !Installing;

    // the page saves it and reads it all back, so a change that didn't save puts the switch back
    public bool On
    {
        get => _on;
        set
        {
            if (value == _on) return;
            if (HasSwitch && SwitchEnabled) _owner.Switch(this, value);
            OnPropertyChanged();
        }
    }

    internal AddonRow Update(RowData d)
    {
        var l = d.Listing;
        Name = l.Name;
        _author = l.Author;
        Details = l.Version is { } v ? l.Author is { } a ? $"{v} by {a}" : v : l.Author is { } by ? "by " + by : null;
        Description = l.Description;
        if (!d.Badges.SequenceEqual(Badges)) Badges = d.Badges;
        HasSwitch = d.HasSwitch;
        CanChange = d.CanChange;
        CanInstall = d.CanInstall;
        Incompatible = d.Incompatible;
        Team = d.Team;
        VerifiedTip = d.Team is { } team ? $"This {(Kind == AddonKind.Addon ? "addon" : "plugin")} is provided and maintained by the {team} development team." : null;
        Location = l.Location;
        if (_on != d.On)
        {
            _on = d.On;
            OnPropertyChanged(nameof(On));
        }
        return this;
    }

    internal bool Matches(string[] words) =>
        words.All(w => Name.Contains(w, StringComparison.OrdinalIgnoreCase) || Key.Contains(w, StringComparison.OrdinalIgnoreCase)
                       || _author?.Contains(w, StringComparison.OrdinalIgnoreCase) == true
                       || Description?.Contains(w, StringComparison.OrdinalIgnoreCase) == true);
}
