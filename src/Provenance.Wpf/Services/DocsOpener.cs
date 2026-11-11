using Provenance.Common.Game.ThirdParty;
using Provenance.Wpf.Views.Dialogs;

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

    public static void ShowText(string title, string text) => ReadmeWindow.Show(WindowOwner.Find(), title, text);
}
