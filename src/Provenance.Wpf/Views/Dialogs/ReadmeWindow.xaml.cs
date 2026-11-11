using System.Windows;
using Provenance.Wpf.Controls;

namespace Provenance.Wpf.Views.Dialogs;

public partial class ReadmeWindow : AppWindow
{
    public ReadmeWindow(string title, string text)
    {
        InitializeComponent();
        Title = title;
        Bar.Title = title;
        BodyText.Text = text;
    }

    public static void Show(Window? owner, string title, string text) => new ReadmeWindow(title, text).ShowDialogOver(owner);
}
