using Provenance.Common.Social;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class FriendsList : UserControl
{
    // the main window's list picks on its own, only the friends window's follows the open conversation
    public static readonly DependencyProperty OwnSelectionProperty = DependencyProperty.Register(
        nameof(OwnSelection), typeof(bool), typeof(FriendsList), new PropertyMetadata(false, (d, e) =>
        {
            if ((bool)e.NewValue) BindingOperations.ClearBinding(((FriendsList)d).List, Selector.SelectedItemProperty);
        }));

    private FriendItem? _kept;

    public FriendsList()
    {
        InitializeComponent();
        ((INotifyCollectionChanged)List.Items).CollectionChanged += OnRowsChanged;
    }

    public bool OwnSelection
    {
        get => (bool)GetValue(OwnSelectionProperty);
        set => SetValue(OwnSelectionProperty, value);
    }

    public event Action<FriendItem>? FriendActivated;

    public event Action<FriendItem>? FriendSelected;

    private SocialViewModel? Vm => DataContext as SocialViewModel;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OwnSelection)
        {
            // a row swapped out by an update drops the selection, which isn't the user picking nobody
            if (List.SelectedItem is FriendItem picked) _kept = picked;
            else if (e.RemovedItems.OfType<object>().All(List.Items.Contains)) _kept = null;
        }
        if (List.IsKeyboardFocusWithin && List.SelectedItem is FriendItem f) FriendSelected?.Invoke(f);
    }

    // the view model keeps its selection across updates, so this list does too once the whole update's in, as a swapped
    // row comes through the grouping as a remove then an add
    private bool _reselecting;
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!OwnSelection || _reselecting || List.SelectedItem is not null || _kept is null) return;
        _reselecting = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
        {
            _reselecting = false;
            if (List.SelectedItem is not null || _kept is not { } was) return;
            _kept = List.Items.OfType<FriendItem>().FirstOrDefault(f => f.Is(was));
            if (_kept is not null) List.SelectedItem = _kept;
        });
    }

    // the row under the pointer, not whatever was selected before
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(List, e.OriginalSource as DependencyObject);
        if (row is ListBoxItem { DataContext: FriendItem f }) Activate(f);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;   // never on to the window's default button (Play)
        if (List.SelectedItem is FriendItem f) Activate(f);
    }

    internal void Activate(FriendItem f)
    {
        if (!f.IsRequest && !f.IsGroupInvite)
        {
            FriendActivated?.Invoke(f);
            return;
        }
        Vm?.AnswerRow(f);
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(List, e.OriginalSource as DependencyObject);
        if (row is not ListBoxItem { DataContext: FriendItem f } || Vm is null)
        {
            e.Handled = true;
            return;
        }
        FillMenu(List.ContextMenu ??= new ContextMenu(), f);
    }

    public void FillMenu(ContextMenu menu, FriendItem f)
    {
        if (Vm is { } vm) Menus.Fill(menu, vm.FriendMenu(f, OpenConversation));
    }

    internal void OpenConversation(FriendItem f) => FriendActivated?.Invoke(f);

    private void OnAddFriend(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) SvgIcon.OpenWhile(sender, () => vm.AddFriend());
    }
}
