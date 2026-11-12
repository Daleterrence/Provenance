using Provenance.Common.Game.ThirdParty;
using Provenance.Wpf.Views.Dialogs;
using Serilog;

namespace Provenance.Wpf.Services;

internal static class DocsOpener
{
    // both through explorer.exe, so the browser starts as the user. A text file isn't opened from here, it's shown
    public static void Open(AddonDocs docs)
    {
        switch (docs.Kind)
        {
            case AddonDocsKind.Link: Explorer.OpenLink(new Uri(docs.Target)); break;
            case AddonDocsKind.Page: Explorer.OpenFile(docs.Target); break;
            default: throw new ArgumentException("Not a link or a page.", nameof(docs));
        }
    }

    public static void ShowText(string title, string text, bool markdown) => ReadmeWindow.Show(WindowOwner.Find(), title, text, markdown);

    public static void OpenReadmeLink(Uri link) => OpenReadmeLink(link, WpfDialogs.Default);

    // explorer.exe reads a comma as the start of another switch, so they're escaped. No opening it from here if explorer won't, that would run as admin
    internal static void OpenReadmeLink(Uri link, IDialogs dialogs)
    {
        try { Explorer.OpenLink(new Uri(link.AbsoluteUri.Replace(",", "%2C", StringComparison.Ordinal))); }
        catch (ArgumentException) { Log.Warning("Not opening a readme link that is not a web link: {Url}", link.OriginalString); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Url} from a readme", link.AbsoluteUri);
            dialogs.Error("Couldn't open the link", $"Windows didn't open the link. You can copy it from here: {link.AbsoluteUri}");
        }
    }
}
