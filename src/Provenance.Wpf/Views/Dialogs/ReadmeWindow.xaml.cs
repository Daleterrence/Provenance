using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
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
        if (markdown && Markdown.Parse(text) is { } doc)
        {
            BodyText.Visibility = Visibility.Collapsed;
            LoadingText.Visibility = Visibility.Visible;
            Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ShowPage(doc, text, openLink ?? DocsOpener.OpenReadmeLink));
        }
        else BodyText.Text = text;
    }

    // the message has been painted by now, laying a big page out is what keeps the window busy
    private void ShowPage(MarkdownDoc doc, string text, Action<Uri> openLink)
    {
        if (!IsLoaded) return;
        if (Laid(doc, openLink) is { } page)
        {
            Page.Document = page;
            Page.Visibility = Visibility.Visible;
            FocusManager.SetFocusedElement(this, Page);
        }
        else
        {
            BodyText.Text = text;
            BodyText.Visibility = Visibility.Visible;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => LoadingText.Visibility = Visibility.Collapsed);
    }

    private static FlowDocument? Laid(MarkdownDoc doc, Action<Uri> openLink)
    {
        try { return ReadmeDocument.Build(doc, openLink); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Log.Warning(e, "Could not lay a readme out, showing it as text");
            return null;
        }
    }

    public static void Show(Window? owner, string title, string text, bool markdown) => new ReadmeWindow(title, text, markdown).ShowDialogOver(owner);
}
