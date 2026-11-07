using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Common.Hook;
using Provenance.Common.Social;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Views.Dialogs;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class AddFriendDialog : AppWindow
{
    private SocialViewModel Vm => (SocialViewModel)DataContext;

    public AddFriendDialog(SocialViewModel vm, string? handleName = null, string? handleKey = null)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += (_, _) => NameBox.Focus();
        // a search still out when it closes is nobody's, so its hits never turn up under whatever's asked next
        Closed += (_, _) => vm.CancelSearch();
        // opened from a row: search their name straight away, then pick their own hit (two people can share a name)
        if (handleName != null)
        {
            vm.CancelSearch();
            vm.SearchKind = FriendSearchKind.Handle;
            NameBox.Text = handleName;
            Loaded += (_, _) => Vm.Search(NameBox.Text);
            if (!string.IsNullOrEmpty(handleKey))
            {
                NotifyCollectionChangedEventHandler pick = (_, _) =>
                {
                    if (Hits.SelectedItem == null && vm.SearchHits.FirstOrDefault(h => h.Key == handleKey) is { } mine)
                        Hits.SelectedItem = mine;
                };
                vm.SearchHits.CollectionChanged += pick;
                Closed += (_, _) => vm.SearchHits.CollectionChanged -= pick;   // the view model outlives the dialog
            }
        }
        Hits.SelectionChanged += (_, _) => UpdateSend();
        PropertyChangedEventHandler busy = (_, e) =>
        {
            if (e.PropertyName == nameof(SocialViewModel.CanEdit)) UpdateSend();
        };
        vm.PropertyChanged += busy;
        Closed += (_, _) => vm.PropertyChanged -= busy;
        Hits.MouseDoubleClick += (s, e) =>
        {
            if (ItemsControl.ContainerFromElement(Hits, e.OriginalSource as DependencyObject) is ListBoxItem)
                OnSend(s, e);
        };
        UpdateSend();
    }

    private void UpdateSend() => SendButton.IsEnabled = Vm.CanEdit && Vm.CanRequest(Hits.SelectedItem as SocialViewModel.SearchHitItem);

    private void OnSearch(object sender, RoutedEventArgs e) => Vm.Search(NameBox.Text);

    private void OnKindPicked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnNameKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Vm.Search(NameBox.Text);
    }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        var hit = Hits.SelectedItem as SocialViewModel.SearchHitItem;
        var problem = Vm.RequestProblem(hit);
        if (problem != null)
        {
            Vm.SearchNote = problem;
            return;
        }
        if (MessageDialog.Confirm(this, "Send friend request",
                $"Send \"Let's be friends!\" to {hit!.Name}? They'll show on your list" +
                (hit.IsCharacter ? " under their PlayOnline handle," : "") + " as \"Friend request sent\" until they accept.",
                "SEND") is not null)
            Vm.SendFriendRequest(hit);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
