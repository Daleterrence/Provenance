using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Provenance.Common.Hook;
using Provenance.Common.Social;
using Provenance.Wpf.Controls;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class AddFriendDialog : AppWindow
{
    private readonly SocialViewModel _social;
    private SocialSearch Search => _social.Search;

    public AddFriendDialog(SocialViewModel vm, string? handleName = null, string? handleKey = null)
    {
        InitializeComponent();
        _social = vm;
        DataContext = vm.Search;
        Loaded += (_, _) => NameBox.Focus();
        Closed += (_, _) => Search.Cancel();
        if (handleName != null)
        {
            Search.Cancel();
            Search.Kind = FriendSearchKind.Handle;
            NameBox.Text = handleName;
            Loaded += (_, _) => Search.Run(NameBox.Text);
            if (!string.IsNullOrEmpty(handleKey))
            {
                NotifyCollectionChangedEventHandler pick = (_, _) =>
                {
                    if (Hits.SelectedItem == null && Search.Hits.FirstOrDefault(h => h.Key == handleKey) is { } mine)
                        Hits.SelectedItem = mine;
                };
                Search.Hits.CollectionChanged += pick;
                Closed += (_, _) => Search.Hits.CollectionChanged -= pick;
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

    private void UpdateSend() => SendButton.IsEnabled = _social.CanEdit && Search.CanRequest(Hits.SelectedItem as SocialSearch.SearchHitItem);

    private void OnSearch(object sender, RoutedEventArgs e) => Search.Run(NameBox.Text);

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
        Search.Run(NameBox.Text);
    }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        var hit = Hits.SelectedItem as SocialSearch.SearchHitItem;
        var problem = Search.RequestProblem(hit);
        if (problem != null)
        {
            Search.Note.Show(problem);
            return;
        }
        Search.AskSendRequest(hit!);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
