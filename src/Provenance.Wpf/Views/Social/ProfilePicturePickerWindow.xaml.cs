using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Common.PolFormat;
using Provenance.Wpf.Services;
using Provenance.Wpf.Controls;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class ProfilePicturePickerWindow : AppWindow
{
    private readonly ProfilePicturePickerViewModel _vm;

    public int? Result => _vm.Result;

    public ProfilePicturePickerWindow(ProfilePictureCatalog.Catalog catalog, AvatarService avatars, int currentPic)
    {
        InitializeComponent();
        _vm = new ProfilePicturePickerViewModel(catalog, avatars.ProfilePicture, currentPic);
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfilePicturePickerViewModel.Tiles)) ShowPick();
        };
        ShowPick();
    }

    private void ShowPick() => Dispatcher.BeginInvoke(() =>
    {
        if (_vm.SelectedTile is { } shown) Pics.ScrollIntoView(shown);
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    private void OnProfilePictureDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(Pics, e.OriginalSource as DependencyObject);
        if (row is ListBoxItem && _vm.CanUse) OnUse(sender, e);
    }

    private void OnUse(object sender, RoutedEventArgs e) => DialogResult = _vm.Use();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
