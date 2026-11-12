using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using Provenance.Common.Game.ThirdParty;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Services;
using Serilog;

namespace Provenance.Wpf.Views.Dialogs;

public partial class ReadmeWindow : AppWindow
{
    public ReadmeWindow(string title, string text, bool markdown = false, Action<Uri>? openLink = null)
    {
        InitializeComponent();
        Title = title;
        Bar.Title = title;
        if (markdown && Laid(text, openLink ?? DocsOpener.OpenReadmeLink) is { } page)
        {
            Page.Document = page;
            Page.Visibility = Visibility.Visible;
            BodyText.Visibility = Visibility.Collapsed;
            FocusManager.SetFocusedElement(this, Page);
        }
        else BodyText.Text = text;
    }

    private static FlowDocument? Laid(string text, Action<Uri> openLink)
    {
        if (Markdown.Parse(text) is not { } doc) return null;
        try { return ReadmeDocument.Build(doc, openLink); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Log.Warning(e, "Could not lay a readme out, showing it as text");
            return null;
        }
    }

    public static void Show(Window? owner, string title, string text, bool markdown) => new ReadmeWindow(title, text, markdown).ShowDialogOver(owner);
}
