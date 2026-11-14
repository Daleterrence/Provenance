using System.Windows;
using Provenance.Wpf.Controls;
using Provenance.Wpf.ViewModels.Main;

namespace Provenance.Wpf.Views.Main;

public partial class AccountsWindow : AppWindow
{
    public AccountsWindow() => InitializeComponent();

    private void OnOrder(object sender, RoutedEventArgs e)
    {
        if (DataContext is AccountsViewModel accounts)
            new AccountOrderWindow { DataContext = new AccountOrderViewModel(accounts) }.ShowDialogOver(this);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
