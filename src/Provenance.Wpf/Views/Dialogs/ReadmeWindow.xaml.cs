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
        if (markdown)
        {
            BodyText.Visibility = Visibility.Collapsed;
            LoadingText.Visibility = Visibility.Visible;
            var cancel = new CancellationTokenSource();
            Closed += (_, _) => cancel.Cancel();
            Loaded += (_, _) => _ = ShowPageAsync(text, openLink ?? DocsOpener.OpenReadmeLink, cancel.Token);
        }
        else BodyText.Text = text;
    }

    private async Task ShowPageAsync(string text, Action<Uri> openLink, CancellationToken cancel)
    {
        try
        {
            var doc = await Task.Run(() => Markdown.Parse(text), cancel);
            var page = await ReadmeDocument.BuildAsync(doc, openLink, cancel);
            Page.Document = page;
            Page.Visibility = Visibility.Visible;
            FocusManager.SetFocusedElement(this, Page);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            LoadingText.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Error(e, "Could not lay a readme out");
            LoadingText.Text = "Couldn't show this file.";
        }
    }

    public static void Show(Window? owner, string title, string text, bool markdown) => new ReadmeWindow(title, text, markdown).ShowDialogOver(owner);
}
