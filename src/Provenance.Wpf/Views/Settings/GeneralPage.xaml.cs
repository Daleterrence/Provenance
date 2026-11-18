using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Provenance.Wpf.Views.Settings;

public partial class GeneralPage : UserControl
{
    public GeneralPage()
    {
        InitializeComponent();
        ThemeTiles.PreviewKeyDown += OnThemeKey;
    }

    // the arrows move between the tiles and pick the one they land on, and Tab only stops on the picked one
    private void OnThemeKey(object sender, KeyEventArgs e)
    {
        int step = e.Key is Key.Right or Key.Down ? 1 : e.Key is Key.Left or Key.Up ? -1 : 0;
        if (step == 0 || e.OriginalSource is not RadioButton from) return;
        var tiles = Enumerable.Range(0, ThemeTiles.Items.Count)
            .Select(i => ThemeTiles.ItemContainerGenerator.ContainerFromIndex(i))
            .Select(c => c is not null && VisualTreeHelper.GetChildrenCount(c) > 0 ? VisualTreeHelper.GetChild(c, 0) as RadioButton : null)
            .OfType<RadioButton>().ToList();
        int at = tiles.IndexOf(from);
        if (at < 0 || tiles.Count < 2) return;
        var next = tiles[(at + step + tiles.Count) % tiles.Count];
        next.IsChecked = true;
        next.Focus();
        e.Handled = true;
    }
}
