using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.ViewModels.Social;

public sealed partial class ProfilePicturePickerViewModel : ObservableObject
{
    private readonly ProfilePictureCatalog.Catalog _catalog;
    private readonly Func<int, ImageSource?> _image;
    private readonly int _current;
    private ProfilePictureTile? _picked;
    private bool _syncing;

    [ObservableProperty] private ProfilePictureCatalog.Category? _category;
    [ObservableProperty] private IReadOnlyList<ProfilePictureTile> _tiles = [];
    [ObservableProperty] private ProfilePictureTile? _selectedTile;
    [ObservableProperty] private string _chosen = "";
    [ObservableProperty] private bool _canUse;

    public int? Result { get; private set; }

    public ProfilePicturePickerViewModel(ProfilePictureCatalog.Catalog catalog, Func<int, ImageSource?> image, int currentPic)
    {
        _catalog = catalog;
        _image = image;
        _current = currentPic;
        var current = catalog.ProfilePictures.FirstOrDefault(p => p.Id == currentPic);
        Category = current is null
            ? catalog.Categories.FirstOrDefault()
            : catalog.Categories.FirstOrDefault(c => c.Key == current.CategoryKey);
        Chosen = current is null ? "" : $"Current: {current.Name}";
    }

    public IReadOnlyList<ProfilePictureCatalog.Category> Categories => _catalog.Categories;

    partial void OnCategoryChanged(ProfilePictureCatalog.Category? value)
    {
        if (value is null) return;
        var tiles = _catalog.ProfilePictures.Where(p => p.CategoryKey == value.Key)
            .Select(p => new ProfilePictureTile(p, _image(p.Id))).ToList();
        foreach (var t in tiles)
            t.IsSelected = _picked != null ? t.ProfilePicture.Id == _picked.ProfilePicture.Id
                                           : t.ProfilePicture.Id == _current;
        if (_picked != null) _picked = tiles.FirstOrDefault(t => t.IsSelected) ?? _picked;
        _syncing = true;
        Tiles = tiles;
        SelectedTile = tiles.FirstOrDefault(t => t.IsSelected);
        _syncing = false;
        if (_picked is null && SelectedTile is { } current)
        {
            _picked = current;
            CanUse = true;
        }
    }

    partial void OnSelectedTileChanged(ProfilePictureTile? value)
    {
        if (_syncing || value is null) return;
        if (_picked != null && !ReferenceEquals(_picked, value)) _picked.IsSelected = false;
        _picked = value;
        value.IsSelected = true;
        Chosen = value.ProfilePicture.Name;
        CanUse = true;
    }

    public bool Use()
    {
        Result = _picked?.ProfilePicture.Id;
        return Result != null && Result != _current;
    }
}
