using System.Windows;
using Provenance.Common.PolFormat;
using Provenance.Wpf.Controls;

namespace Provenance.Wpf.Views.Dialogs;

public partial class AgreementDialog : AppWindow
{
    public AgreementDialog(IReadOnlyDictionary<int, string>? strings)
    {
        InitializeComponent();
        bool none = strings != null && strings.TryGetValue(PolStringTable.AgreementText, out var text) && text == "\u672a\u7ffb\u8a33";
        string Get(int id) => !none && strings != null && strings.TryGetValue(id, out var s) ? s : "";
        var body = Get(PolStringTable.AgreementText);
        var article8 = Get(PolStringTable.AgreementArticle8);
        Bar.Title = body.Length > 0 ? body.Split('\n')[0].Trim() : "PlayOnline Member Agreement";
        var notice = Get(PolStringTable.AgreementNotice);
        IntroText.Text = $"{(notice.Length > 0 ? notice + ": " : "")}You need to accept this agreement before Provenance can log in " +
                         "to PlayOnline. Read it, then choose below.";
        BodyText.Text = body.Length > 0
            ? body + (article8.Length > 0 ? "\n\n" + article8 : "")
            : "Provenance couldn't find the PlayOnline Member Agreement in your PlayOnline Viewer install. " +
              "If you don't accept it, Provenance won't log in.";
    }

    public static bool Ask(Window? owner, IReadOnlyDictionary<int, string>? strings)
    {
        var dlg = new AgreementDialog(strings);
        return dlg.ShowDialogOver(owner) == true;
    }

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnDecline(object sender, RoutedEventArgs e) => DialogResult = false;
}
