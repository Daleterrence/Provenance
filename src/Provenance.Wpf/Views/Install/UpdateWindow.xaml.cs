using System.Windows;
using System.Windows.Input;
using Provenance.Wpf.Controls;

namespace Provenance.Wpf.Views.Install;

public partial class UpdateWindow : Window
{
    public event EventHandler? SkipClicked;

    public UpdateWindow()
    {
        InitializeComponent();
        WindowFrame.Attach(this);
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
        SkipButton.Visibility = Visibility.Collapsed;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        SkipButton.IsEnabled = false;
        SkipClicked?.Invoke(this, EventArgs.Empty);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
