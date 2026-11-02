using System.IO;
using System.Windows;
using System.Windows.Input;
using MaterialDesignThemes.Wpf;

namespace Provenance.Wpf.Views;

public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
        WindowFrame.Attach(this);
    }

    public static void ShowError(Window? owner, string title, string body, bool offerLogs = false) =>
        Create(title, body, null, offerLogs).ShowOver(owner);

    public static void ShowInfo(Window? owner, string title, string body) =>
        Create(title, body, PackIconKind.InformationOutline).ShowOver(owner);

    // Null when cancelled, else 0 (or 1 for the second of two choices). Destructive and safeKeys ones put Enter and Esc
    // on cancel so a stray key can't do it, and only destructive goes red.
    public static int? Confirm(Window? owner, string title, string body, string okLabel,
                               string? choiceA = null, string? choiceB = null, bool destructive = false, bool safeKeys = false,
                               string? cancelLabel = null)
    {
        var dlg = ForConfirm(title, body, okLabel, choiceA, choiceB, destructive, safeKeys, cancelLabel);
        if (dlg.ShowOver(owner) != true) return null;
        return dlg.ChoiceB.IsChecked == true && dlg.Choices.Visibility == Visibility.Visible ? 1 : 0;
    }

    // set up but not shown yet
    internal static MessageDialog ForConfirm(string title, string body, string okLabel,
                                             string? choiceA = null, string? choiceB = null, bool destructive = false, bool safeKeys = false,
                                             string? cancelLabel = null)
    {
        var dlg = Create(title, body, PackIconKind.HelpCircleOutline);
        dlg.CancelButton.Visibility = Visibility.Visible;
        if (cancelLabel != null) dlg.CancelButton.Content = cancelLabel;
        dlg.OkButton.Content = okLabel;
        dlg.OkButton.IsCancel = false;
        dlg.CancelButton.IsCancel = true;
        if (destructive || safeKeys)
        {
            dlg.OkButton.IsDefault = false;
            dlg.CancelButton.IsDefault = true;
            dlg.Loaded += (_, _) => dlg.CancelButton.Focus();
        }
        if (destructive) dlg.OkButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "State.Down");
        if (choiceA != null && choiceB != null)
        {
            dlg.ChoiceA.Content = choiceA;
            dlg.ChoiceB.Content = choiceB;
            dlg.Choices.Visibility = Visibility.Visible;
        }
        return dlg;
    }

    // a null icon keeps the red error mark from the XAML
    private static MessageDialog Create(string title, string body, PackIconKind? icon, bool offerLogs = false)
    {
        var dlg = new MessageDialog();
        dlg.TitleText.Text = title;
        dlg.BodyText.Text = body;
        if (!offerLogs) dlg.LogsButton.Visibility = Visibility.Collapsed;
        if (icon is { } kind)
        {
            dlg.KindIcon.Kind = kind;
            dlg.KindIcon.Foreground = (System.Windows.Media.Brush)dlg.FindResource("Primary");
        }
        return dlg;
    }

    private bool? ShowOver(Window? owner)
    {
        if (owner is { IsVisible: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return ShowDialog();
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Provenance", "logs");
        if (Directory.Exists(dir)) Services.Explorer.ShowFolder(dir);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
