using System.Windows;
using System.Windows.Controls;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class RequestsList : UserControl
{
    public RequestsList() => InitializeComponent();

    private void OnAnswer(object sender, RoutedEventArgs e)
    {
        if (DataContext is SocialViewModel vm && sender is Button { DataContext: FriendItem f, Tag: RequestChoice choice })
            vm.AnswerWith(f, choice);
    }
}
