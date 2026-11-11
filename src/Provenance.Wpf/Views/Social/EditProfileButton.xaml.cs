using System.Windows.Controls;
using System.Windows.Input;

namespace Provenance.Wpf.Views.Social;

public partial class EditProfileButton : UserControl
{
    public EditProfileButton() => InitializeComponent();

    // the second press of a double-click would close the editor it just opened
    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1) e.Handled = true;
    }
}
