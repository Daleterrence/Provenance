using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.Game.ThirdParty;

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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Greyed))] private bool _incompatible;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Greyed))] private bool _replacedByAutoLogin;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Greyed))] private AddonGroup? _group;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SwitchEnabled))] private bool _installing;
    [ObservableProperty] private bool _visible = true;
    [ObservableProperty] private string? _verifiedTip;
    [ObservableProperty] private string? _team;
    [ObservableProperty] private string? _location;
    [ObservableProperty] private AddonDocs? _docs;
    private string? _author;

    public bool SwitchEnabled => CanChange && !Installing && !_owner.Busy;

    internal void OwnerBusyChanged() => OnPropertyChanged(nameof(SwitchEnabled));
    public bool Greyed => Incompatible || ReplacedByAutoLogin || Group is not null;

    public bool On
    {
        get => _on;
        set
        {
            if (value == _on) return;
            if (HasSwitch && SwitchEnabled)
            {
                var before = _on;
                _on = value;
                if (!_owner.Switch(this, value)) _on = before;
            }
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
        ReplacedByAutoLogin = d.ReplacedByAutoLogin;
        Group = d.Group;
        Team = d.Team;
        VerifiedTip = d.Team is { } team ? $"This {(Kind == AddonKind.Addon ? "addon" : "plugin")} is in {team}'s official list." : null;
        Location = l.Location;
        Docs = l.Docs;
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
