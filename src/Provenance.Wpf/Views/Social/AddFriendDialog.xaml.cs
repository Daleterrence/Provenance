using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Common.Social;
using Provenance.Wpf.ViewModels;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Views.Dialogs;

namespace Provenance.Wpf.Views.Social;

public partial class AddFriendDialog : Window
{
    private SocialViewModel Vm => (SocialViewModel)DataContext;

    public AddFriendDialog(SocialViewModel vm, string? handleName = null, string? handleKey = null)
    {
        InitializeComponent();
        WindowFrame.Attach(this);
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
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
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

    private void UpdateSend() => SendButton.IsEnabled = Vm.CanEdit &&
        Hits.SelectedItem is SocialViewModel.SearchHitItem { IsFriend: false, RequestSent: false, IsMe: false };

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
        var problem = hit switch
        {
            null => Vm.SearchKind == FriendSearchKind.Character ? "Choose a character from the list first." : "Choose a handle from the list first.",
            { IsMe: true } => "That's one of your own handles. You can't add yourself as a friend.",
            { IsFriend: true } => $"{hit.Name} is already on your friend list.",
            { RequestSent: true } => $"You've already sent {hit.Name} a friend request.",
            _ => Vm.FriendRequestBlocked,
        };
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
