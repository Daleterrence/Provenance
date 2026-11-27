using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Provenance.Common.Settings;

namespace Provenance.Wpf.ViewModels.Main;

public sealed partial class AccountOrderItem(Account account, string name, ImageSource? picture) : ObservableObject
{
    public Account Account { get; } = account;
    public ImageSource? Picture { get; } = picture;

    [ObservableProperty] private string _name = name;
    [ObservableProperty] private bool _canMoveUp;
    [ObservableProperty] private bool _canMoveDown;
}

public sealed partial class AccountOrderViewModel : ObservableObject
{
    private readonly AccountsViewModel _accounts;

    public AccountOrderViewModel(AccountsViewModel accounts)
    {
        _accounts = accounts;
        Items = new(accounts.Rows.Select(r => new AccountOrderItem(r.Account, r.Name, r.Picture)));
        Refresh();
    }

    public ObservableCollection<AccountOrderItem> Items { get; }

    [RelayCommand]
    private void MoveUp(AccountOrderItem? item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(AccountOrderItem? item) => Move(item, 1);

    private void Move(AccountOrderItem? item, int by)
    {
        if (item is null) return;
        int from = Items.IndexOf(item);
        if (from < 0 || !_accounts.Move(item.Account, by)) return;
        Items.Move(from, from + by);
        Refresh();
    }

    private void Refresh()
    {
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].CanMoveUp = i > 0;
            Items[i].CanMoveDown = i < Items.Count - 1;
            Items[i].Name = _accounts.NameOf(Items[i].Account);
        }
    }
}
