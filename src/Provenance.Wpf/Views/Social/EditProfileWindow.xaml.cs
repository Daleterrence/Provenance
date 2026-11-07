using System.Windows;
using System.Windows.Input;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Views.Dialogs;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class EditProfileWindow : AppWindow
{
    private readonly EditProfileViewModel _vm;
    private bool _closeQuietly;

    public EditProfileWindow(SocialViewModel social)
    {
        InitializeComponent();
        DataContext = _vm = new EditProfileViewModel(social);
        _vm.ConfirmRename = (was, now) => MessageDialog.Confirm(this, "Rename your handle",
            $"Friends will see {now} instead of {was} the next time their friend list loads. You can rename it again later.", "RENAME") is not null;
        // 1366x768, or 1080p with scaling, is shorter than 780
        MaxHeight = SystemParameters.WorkArea.Height;
        if (Height > MaxHeight - 20) Height = MaxHeight - 20;
        _vm.SaveRefused += text => MessageDialog.ShowError(this, "Not saved", text);
        Closing += OnClosing;
        Closed += (_, _) => _vm.Dispose();
        Loaded += (_, _) => _vm.Load();
    }

    // never parented to the friends window, so closing that can't take unsaved changes down with it
    public static void ShowFor(SocialViewModel social, Window? near)
    {
        if (Current is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var w = new EditProfileWindow(social);
        if (near is { IsVisible: true, WindowState: WindowState.Normal })
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = near.Left + (near.ActualWidth - w.Width) / 2;
            w.Top = Math.Max(SystemParameters.VirtualScreenTop, near.Top + (near.ActualHeight - w.Height) / 2);
        }
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Show();
    }

    public static EditProfileWindow? Current =>
        Application.Current?.Windows.OfType<EditProfileWindow>().FirstOrDefault();

    // a load has nothing of theirs in it yet, closing just withdraws the request
    private bool Saving => _vm.Busy && !_vm.LoadingProfile;

    public bool HasUnsavedWork => Saving || _vm.HasChanges;

    // the caller already asked (sign-out, exit), or the session's gone and nothing can be saved
    public void CloseQuietly()
    {
        _closeQuietly = true;
        Close();
    }

    // before sign-out or exit: asks if there's unsaved work, then closes the editor. False means they backed out
    public static bool ConfirmDrop(string action)
    {
        if (Current is not { } ed) return true;
        if (ed.HasUnsavedWork)
        {
            if (ed.WindowState == WindowState.Minimized) ed.WindowState = WindowState.Normal;
            ed.Activate();
            if (MessageDialog.Confirm(ed, "Unsaved profile changes",
                    $"You haven't saved your profile changes. {action} anyway? They'll be lost.", action.ToUpperInvariant(),
                    destructive: true) is null)
                return false;
        }
        ed.CloseQuietly();
        return true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeQuietly) return;
        if (Saving)
        {
            // closing drops the save's result, and a save POL hasn't taken yet gets withdrawn
            if (MessageDialog.Confirm(this, "Saving your profile",
                    "Your changes are on their way to PlayOnline. Close anyway? The save might not finish.", "CLOSE",
                    destructive: true) is null)
                e.Cancel = true;
            return;
        }
        if (!_vm.HasChanges) return;
        if (MessageDialog.Confirm(this, "Unsaved profile changes", "Close without saving your changes?", "DISCARD",
                                  destructive: true) is null)
            e.Cancel = true;
    }

    private void OnProfilePictureClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Social.ChangeProfilePictureCommand.CanExecute(null))
            _vm.Social.ChangeProfilePictureCommand.Execute(null);
    }

    private void OnDigitsOnly(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void OnCardsWheel(object sender, MouseWheelEventArgs e)
    {
        if (CardStrip.ScrollableWidth <= 0) return;
        CardStrip.ScrollToHorizontalOffset(CardStrip.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
