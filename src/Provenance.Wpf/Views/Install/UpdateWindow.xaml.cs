using System.Windows;
using Provenance.Wpf.Controls;

namespace Provenance.Wpf.Views.Install;

public partial class UpdateWindow : AppWindow
{
    private TaskCompletionSource<bool>? _answer;

    public UpdateWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _answer?.TrySetResult(false);
    }

    // the Cancel on the wait for other copies to close. A close with a question up answers it, so this is only for the wait
    public event EventHandler? CancelRequested;

    public void ShowChecking()
    {
        Choices.Visibility = Visibility.Collapsed;
        StageText.Text = "Checking for Provenance updates...";
        Bar.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;
        DetailText.Text = "";
    }

    public void ShowDownload(string version)
    {
        StageText.Text = $"Downloading Provenance {version}";
        Bar.IsIndeterminate = false;
        Bar.Value = 0;
        DetailText.Text = "0%";
    }

    public void ShowProgress(int percent)
    {
        Bar.Value = percent;
        DetailText.Text = $"{percent}%";
    }

    public void ShowWaitingToClose()
    {
        TryAgainButton.Visibility = Visibility.Collapsed;
        CloseButton.Content = "CANCEL";
        Choices.Visibility = Visibility.Visible;
        CloseButton.Focus();
        StageText.Text = "Waiting for Provenance to close...";
        Bar.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;
        DetailText.Text = "Any other copy of Provenance open is being asked to close first.";
    }

    public void ShowInstalling()
    {
        Choices.Visibility = Visibility.Collapsed;
        StageText.Text = "Installing the update...";
        Bar.IsIndeterminate = true;
        DetailText.Text = "Provenance will restart once the update's installed.";
    }

    // true for Try again, false for Close or the window closing
    public Task<bool> AskTryAgain(string title, string detail, bool canRetry = true)
    {
        StageText.Text = title;
        DetailText.Text = detail;
        Bar.Visibility = Visibility.Collapsed;
        TryAgainButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Content = "CLOSE";
        Choices.Visibility = Visibility.Visible;
        (canRetry ? TryAgainButton : CloseButton).Focus();
        _answer = new TaskCompletionSource<bool>();
        return _answer.Task;
    }

    private void OnTryAgain(object sender, RoutedEventArgs e) => Answer(true);

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_answer is null)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        Answer(false);
    }

    private void Answer(bool tryAgain)
    {
        Choices.Visibility = Visibility.Collapsed;
        _answer?.TrySetResult(tryAgain);
        _answer = null;
    }
}
