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

    internal static MessageDialog ForNotice(string title, string body, bool offerLogs = false) => Create(title, body, notice: true, offerLogs);

    internal static int? Choose(Window? owner, string title, string body, string ok, string first, string second, ConfirmStyle style)
    {
        var dlg = ForConfirm(title, body, ok, style, null, first, second);
        if (dlg.ShowDialogOver(owner) != true) return null;
        return dlg.ChoiceB.IsChecked == true ? 1 : 0;
    }

    internal static MessageDialog ForConfirm(string title, string body, string ok, ConfirmStyle style = ConfirmStyle.Normal,
                                             string? cancelLabel = null, string? choiceA = null, string? choiceB = null)
    {
        // a plain question keeps its question mark, one that deletes something is a warning
        var dlg = Create(title, body, notice: style == ConfirmStyle.Destructive);
        dlg.CancelButton.Visibility = Visibility.Visible;
        if (cancelLabel != null) dlg.CancelButton.Content = cancelLabel;
        dlg.OkButton.Content = ok;
        dlg.OkButton.IsCancel = false;
        dlg.CancelButton.IsCancel = true;
        if (style != ConfirmStyle.Normal)
        {
            dlg.OkButton.IsDefault = false;
            dlg.CancelButton.IsDefault = true;
            dlg.Loaded += (_, _) => dlg.CancelButton.Focus();
        }
        // one red button per dialog: the delete when there is one, otherwise cancel. A No answering a question stays blue
        if (style == ConfirmStyle.Destructive) dlg.OkButton.SetResourceReference(StyleProperty, "RedButton");
        if (style == ConfirmStyle.Destructive || cancelLabel != null) dlg.CancelButton.SetResourceReference(StyleProperty, "PolButton");
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
