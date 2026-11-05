using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Common.PolFormat;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.Views;

public partial class ProfilePicturePickerWindow : Window
{
    private readonly ProfilePictureCatalog.Catalog _catalog;
    private readonly AvatarService _avatars;
    private readonly int _current;
    private ProfilePictureTile? _selected;
    private bool _syncing;   // showing the pick again isn't a new pick

    public int? Result { get; private set; }

    public ProfilePicturePickerWindow(ProfilePictureCatalog.Catalog catalog, AvatarService avatars, int currentPic)
    {
        InitializeComponent();
        WindowFrame.Attach(this);
        _catalog = catalog;
        _avatars = avatars;
        _current = currentPic;
        Categories.ItemsSource = catalog.Categories;
        var current = catalog.ProfilePictures.FirstOrDefault(p => p.Id == currentPic);
        Categories.SelectedItem = current is null
            ? catalog.Categories.FirstOrDefault()
            : catalog.Categories.FirstOrDefault(c => c.Key == current.CategoryKey);
        Chosen.Text = current is null ? "" : $"Current: {current.Name}";
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Categories.SelectedItem is not ProfilePictureCatalog.Category cat) return;
        var tiles = _catalog.ProfilePictures.Where(p => p.CategoryKey == cat.Key)
            .Select(p => new ProfilePictureTile(p, _avatars.ProfilePicture(p.Id))).ToList();
        foreach (var t in tiles)
            t.IsSelected = _selected != null ? t.ProfilePicture.Id == _selected.ProfilePicture.Id
                                             : t.ProfilePicture.Id == _current;
        if (_selected != null) _selected = tiles.FirstOrDefault(t => t.IsSelected) ?? _selected;
        _syncing = true;
        Pics.ItemsSource = tiles;
        Pics.SelectedItem = tiles.FirstOrDefault(t => t.IsSelected);
        _syncing = false;
        // the current pic counts as a pick too, Use keeps it and nothing gets saved
        if (_selected is null && Pics.SelectedItem is ProfilePictureTile current)
        {
            _selected = current;
            UseButton.IsEnabled = true;
        }
        // the picked one can be a long way down a big category
        if (Pics.SelectedItem is { } shown)
            Dispatcher.BeginInvoke(() => Pics.ScrollIntoView(shown),
                                   System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnProfilePictureSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || Pics.SelectedItem is not ProfilePictureTile tile) return;
        if (_selected != null && !ReferenceEquals(_selected, tile)) _selected.IsSelected = false;
        _selected = tile;
        tile.IsSelected = true;
        Chosen.Text = tile.ProfilePicture.Name;
        UseButton.IsEnabled = true;
    }

    private void OnProfilePictureDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(Pics, e.OriginalSource as DependencyObject);
        if (row is ListBoxItem && _selected != null) OnUse(sender, e);
    }

    // the pic already in use means nothing to save, so nothing goes to POL
    private void OnUse(object sender, RoutedEventArgs e)
    {
        Result = _selected?.ProfilePicture.Id;
        DialogResult = Result != null && Result != _current;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
