using System.Windows;
using Provenance.Wpf.Controls;

namespace Provenance.Wpf.Views.Main;

public partial class AccountsWindow : AppWindow
{
    public AccountsWindow() => InitializeComponent();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
