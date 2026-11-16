using System.Windows;
using System.Windows.Input;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class EditProfileWindow : AppWindow
{
    private readonly EditProfileViewModel _vm;
    private bool _closeQuietly, _asking, _answered, _closed;

    public EditProfileWindow(SocialViewModel social, IDialogs dialogs)
    {
        InitializeComponent();
        DataContext = _vm = new EditProfileViewModel(social, dialogs);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            _vm.Dispose();
        };
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

    // the question is asked after a turned down close, a window stuck inside Closing can't be closed by the session ending
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeQuietly || _answered) return;
        if (WindowOwner.AppClosing)
        {
            if (!_vm.ConfirmClose()) e.Cancel = true;
            return;
        }
        if (!_vm.HasUnsavedWork) return;
        e.Cancel = true;
        if (_asking) return;
        _asking = true;
        Dispatcher.BeginInvoke(AskToClose);
    }

    private void AskToClose()
    {
        try
        {
            if (_closed) return;
            if (!_vm.ConfirmClose()) return;
        }
        finally { _asking = false; }
        // the session can have taken the window while the question was up
        if (_closed) return;
        _answered = true;
        try { Close(); }
        finally { _answered = false; }
    }

    private void OnDigitsOnly(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void OnCardsWheel(object sender, MouseWheelEventArgs e)
    {
        if (CardStrip.ScrollableWidth <= 0) return;
        CardStrip.ScrollToHorizontalOffset(CardStrip.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
