using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Provenance.Wpf.ViewModels.Shared;
using Provenance.Wpf.Views.Dialogs;

namespace Provenance.Wpf.Controls.Behaviours;

// a click on the button of an open menu or popup closes it, and this stops that same click opening it again. Dumb, but
// it works
public sealed class ReopenGuard
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(300);
    private DateTime _closedAt;

    public void Closed() => _closedAt = DateTime.UtcNow;

    public void OnClosed(object? sender, EventArgs e) => Closed();

    public bool JustClosed => DateTime.UtcNow - _closedAt < Window;
}

public static class Menus
{
    public static MenuItem Item(string header, bool enabled, Action click)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => click();
        return item;
    }

    // Enter and Esc on cancel
    public static Func<MenuConfirm, bool> Confirm(Window? owner) => q =>
        MessageDialog.Confirm(owner, q.Title, q.Body, q.Ok, destructive: true) is not null;

    public static void Fill(ContextMenu menu, IEnumerable<MenuAction> actions)
    {
        menu.Items.Clear();
        foreach (var action in actions) menu.Items.Add(Build(action));
    }

    // Copy first, then the line's own actions
    public static void FillLine(ContextMenu menu, TextBox? box, IEnumerable<MenuAction> actions)
    {
        Fill(menu, actions);
        menu.Items.Insert(0, new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy, CommandTarget = box });
    }

    public static void ShowBelow(ContextMenu menu, FrameworkElement button) => Show(menu, button, PlacementMode.Bottom);

    public static void Show(ContextMenu menu, FrameworkElement target, PlacementMode placement)
    {
        menu.PlacementTarget = target;
        menu.Placement = placement;
        menu.IsOpen = true;
    }

    private static object Build(MenuAction action)
    {
        if (action.IsSeparator) return new Separator();
        var item = new MenuItem { Header = Header(action), IsEnabled = action.Enabled };
        if (action.Checked is { } on)
        {
            item.IsCheckable = true;
            item.IsChecked = on;
        }
        if (action.Items is { } sub)
        {
            foreach (var s in sub) item.Items.Add(Build(s));
            if (sub.Count == 0) item.IsEnabled = false;
        }
        if (action.Run is { } run) item.Click += (_, _) => run();
        if (action.Tip is { } tip)
        {
            item.ToolTip = tip;
            ToolTipService.SetShowOnDisabled(item, true);
        }
        return item;
    }

    private static object Header(MenuAction action)
    {
        if (action.Detail is not { } detail) return action.Header;
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = action.Header, FontSize = 13 });
        if (detail.Length > 0)
            header.Children.Add(new TextBlock { Text = detail, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 280 });
        return header;
    }
}
