using System.Windows;
using Provenance.Wpf.Controls;

namespace Provenance.Wpf.Views.Main;

public partial class AccountOrderWindow : AppWindow
{
    public AccountOrderWindow() => InitializeComponent();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
