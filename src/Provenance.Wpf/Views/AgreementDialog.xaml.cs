using System.Windows;
using System.Windows.Input;
using Provenance.Common.PolFormat;

namespace Provenance.Wpf.Views;

// the text comes from the user's own POL install (StringTable.bin 0x7011 to 0x7013), nothing's shipped
public partial class AgreementDialog : Window
{
    public AgreementDialog(string accountName, IReadOnlyDictionary<int, string>? strings)
    {
        InitializeComponent();
        WindowFrame.Attach(this);
        // the JP table only has "untranslated" placeholders where the agreement goes, so there's nothing of it to show
        bool none = strings != null && strings.TryGetValue(PolStringTable.AgreementText, out var text) && text == "\u672a\u7ffb\u8a33";
        string Get(int id) => !none && strings != null && strings.TryGetValue(id, out var s) ? s : "";
        var body = Get(PolStringTable.AgreementText);
        var article8 = Get(PolStringTable.AgreementArticle8);
        TitleText.Text = body.Length > 0 ? body.Split('\n')[0].Trim() : "PlayOnline Member Agreement";
        var notice = Get(PolStringTable.AgreementNotice);
        IntroText.Text = $"{(notice.Length > 0 ? notice + ": " : "")}PlayOnline asks every account to accept this agreement " +
                         $"before it logs in for the first time. Read it, then choose below{(accountName.StartsWith("New account", StringComparison.Ordinal) ? "" : " for " + accountName)}.";
        BodyText.Text = body.Length > 0
            ? body + (article8.Length > 0 ? "\n\n" + article8 : "")
            : "Provenance couldn't read the PlayOnline Member Agreement from your PlayOnline Viewer install. " +
              "It's the same agreement the PlayOnline Viewer shows before an account logs in for the first time. " +
              "If you don't accept it, Provenance won't log in.";
    }

    public static bool Ask(Window? owner, string accountName, IReadOnlyDictionary<int, string>? strings)
    {
        var dlg = new AgreementDialog(accountName, strings);
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true;
    }

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnDecline(object sender, RoutedEventArgs e) => DialogResult = false;

    // a read-only text box only moves a hidden caret on these keys
    private void OnBodyKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Down: BodyText.LineDown(); break;
            case Key.Up: BodyText.LineUp(); break;
            case Key.PageDown: BodyText.PageDown(); break;
            case Key.PageUp: BodyText.PageUp(); break;
            default: return;
        }
        e.Handled = true;
    }
}
