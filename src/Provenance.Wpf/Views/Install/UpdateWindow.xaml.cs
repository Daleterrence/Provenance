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

    public void ShowInstalling()
    {
        StageText.Text = "Installing the update...";
        Bar.IsIndeterminate = true;
        DetailText.Text = "Provenance will restart once the update's installed.";
    }

    // true for Try again, false for Close or the window closing
    public Task<bool> AskTryAgain(string title, string detail)
    {
        StageText.Text = title;
        DetailText.Text = detail;
        Bar.Visibility = Visibility.Collapsed;
        Choices.Visibility = Visibility.Visible;
        TryAgainButton.Focus();
        _answer = new TaskCompletionSource<bool>();
        return _answer.Task;
    }

    private void OnTryAgain(object sender, RoutedEventArgs e) => Answer(true);

    private void OnClose(object sender, RoutedEventArgs e) => Answer(false);

    private void Answer(bool tryAgain)
    {
        Choices.Visibility = Visibility.Collapsed;
        _answer?.TrySetResult(tryAgain);
        _answer = null;
    }
}
