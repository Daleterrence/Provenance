using System.Windows;
using System.Windows.Input;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class EditProfileWindow : AppWindow
{
    private readonly EditProfileViewModel _vm;
    private bool _closeQuietly;

    public EditProfileWindow(SocialViewModel social, IDialogs dialogs)
    {
        InitializeComponent();
        DataContext = _vm = new EditProfileViewModel(social, dialogs);
        // 1366x768, or 1080p with scaling, is shorter than 780
        MaxHeight = SystemParameters.WorkArea.Height;
        if (Height > MaxHeight - 20) Height = MaxHeight - 20;
        Closing += OnClosing;
        Closed += (_, _) => _vm.Dispose();
        Loaded += (_, _) => _vm.Load();
    }

    internal EditProfileViewModel Editor => _vm;

    // the caller already asked (log out, exit), or the session's gone and nothing can be saved
    public void CloseQuietly()
    {
        _closeQuietly = true;
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeQuietly && !_vm.ConfirmClose()) e.Cancel = true;
    }

    private void OnDigitsOnly(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void OnCardsWheel(object sender, MouseWheelEventArgs e)
    {
        if (CardStrip.ScrollableWidth <= 0) return;
        CardStrip.ScrollToHorizontalOffset(CardStrip.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
