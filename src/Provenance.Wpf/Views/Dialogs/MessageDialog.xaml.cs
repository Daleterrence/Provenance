using System.IO;
using System.Windows;
using System.Windows.Input;
using Provenance.Common;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Services;

namespace Provenance.Wpf.Views.Dialogs;

public partial class MessageDialog : AppWindow
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    public static void ShowError(Window? owner, string title, string body, bool offerLogs = false) =>
        ForNotice(title, body, offerLogs).ShowDialogOver(owner);

    public static void ShowInfo(Window? owner, string title, string body) =>
        ForNotice(title, body).ShowDialogOver(owner);

    // an error or a notice, set up but not shown yet
    internal static MessageDialog ForNotice(string title, string body, bool offerLogs = false) => Create(title, body, notice: true, offerLogs);

    // Null when cancelled, else 0 (or 1 for the second of two choices). Destructive and safeKeys ones put Enter and Esc
    // on cancel so a stray key can't do it, and only destructive goes red.
    public static int? Confirm(Window? owner, string title, string body, string okLabel,
                               string? choiceA = null, string? choiceB = null, bool destructive = false, bool safeKeys = false,
                               string? cancelLabel = null)
    {
        var dlg = ForConfirm(title, body, okLabel, choiceA, choiceB, destructive, safeKeys, cancelLabel);
        if (dlg.ShowDialogOver(owner) != true) return null;
        return dlg.ChoiceB.IsChecked == true && dlg.Choices.Visibility == Visibility.Visible ? 1 : 0;
    }

    // set up but not shown yet
    internal static MessageDialog ForConfirm(string title, string body, string okLabel,
                                             string? choiceA = null, string? choiceB = null, bool destructive = false, bool safeKeys = false,
                                             string? cancelLabel = null)
    {
        // a plain question keeps its question mark, one that deletes something is a warning
        var dlg = Create(title, body, notice: destructive);
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
        // one red button per dialog: the delete when there is one, otherwise cancel. A No answering a question stays blue
        if (destructive) dlg.OkButton.SetResourceReference(StyleProperty, "RedButton");
        if (destructive || cancelLabel != null) dlg.CancelButton.SetResourceReference(StyleProperty, "PolButton");
        if (choiceA != null && choiceB != null)
        {
            dlg.ChoiceA.Content = choiceA;
            dlg.ChoiceB.Content = choiceB;
            dlg.Choices.Visibility = Visibility.Visible;
        }
        return dlg;
    }

    private static MessageDialog Create(string title, string body, bool notice, bool offerLogs = false)
    {
        var dlg = new MessageDialog();
        dlg.TitleText.Text = title;
        dlg.BodyText.Text = body;
        if (!offerLogs) dlg.LogsButton.Visibility = Visibility.Collapsed;
        if (notice && SvgIcons.Notice is { } art)
        {
            dlg.NoticeArt.Source = art;
            dlg.NoticeArt.Visibility = Visibility.Visible;
            dlg.KindIcon.Visibility = Visibility.Collapsed;
        }
        return dlg;
    }

    internal bool ShowsNotice => NoticeArt.Visibility == Visibility.Visible;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        var dir = AppPaths.Logs;
        if (Directory.Exists(dir)) Explorer.ShowFolder(dir);
    }
}
