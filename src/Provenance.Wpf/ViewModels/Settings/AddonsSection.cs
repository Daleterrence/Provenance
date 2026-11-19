using CommunityToolkit.Mvvm.ComponentModel;

namespace Provenance.Wpf.ViewModels.Settings;

public sealed record AddonGroup(string Title, string Help);

public sealed partial class AddonsSection : ObservableObject
{
    private readonly AddonsPluginsViewModel _page;

    internal AddonsSection(AddonsPluginsViewModel page, AddonGroup group)
    {
        _page = page;
        Group = group;
    }

    public AddonGroup Group { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Header))] private int _count;

    public string Header => $"{Group.Title} ({Count})";

    public bool IsOpen
    {
        get => _page.SectionsOpen;
        set => _page.FoldSections(!value);
    }

    internal void FoldChanged() => OnPropertyChanged(nameof(IsOpen));
}
