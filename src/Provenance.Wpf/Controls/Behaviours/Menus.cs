using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Provenance.Wpf.ViewModels.Shared;

namespace Provenance.Wpf.Controls.Behaviours;

public static class Menus
{
    public static MenuItem Item(string header, bool enabled, Action click)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => click();
        return item;
    }

    public static void Fill(ContextMenu menu, IEnumerable<MenuAction> actions)
    {
        menu.Items.Clear();
        foreach (var action in actions) menu.Items.Add(Build(action));
    }

    public static void FillLine(ContextMenu menu, TextBox? box, IEnumerable<MenuAction> actions)
    {
        Fill(menu, actions);
        menu.Items.Insert(0, new MenuItem { Header = "Copy", Command = ApplicationCommands.Copy, CommandTarget = box });
    }

    private static FrameworkElement? _from, _shutBy;
    private static DependencyObject? _open;

    internal static Func<FrameworkElement, bool> PressIsOn = Pressed;

    internal static bool OpenFrom(FrameworkElement button) =>
        ReferenceEquals(_from, button) && _open is { } open && (bool)open.GetValue(Popup.IsOpenProperty);

    public static void Toggle(FrameworkElement button, Func<ContextMenu?> build, PlacementMode placement = PlacementMode.Bottom) =>
        Flip(button, () =>
        {
            if (build() is not { } menu) return null;
            menu.PlacementTarget = button;
            menu.Placement = placement;
            return menu;
        });

    public static void Toggle(FrameworkElement button, Func<Popup?> build) => Flip(button, build);

    private static void Flip(FrameworkElement button, Func<DependencyObject?> build)
    {
        if (ReferenceEquals(_shutBy, button))
        {
            _shutBy = null;
            return;
        }
        bool mine = OpenFrom(button);
        _open?.SetValue(Popup.IsOpenProperty, false);
        _shutBy = null;
        if (mine || build() is not { } menu) return;
        menu.SetValue(Popup.IsOpenProperty, true);
        if (!(bool)menu.GetValue(Popup.IsOpenProperty)) return;
        _from = button;
        _open = menu;
        var isOpen = DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty, menu.GetType());
        EventHandler? changed = null;
        changed = (_, _) =>
        {
            if ((bool)menu.GetValue(Popup.IsOpenProperty)) return;
            isOpen.RemoveValueChanged(menu, changed!);
            if (ReferenceEquals(_open, menu) && ReferenceEquals(_from, button)) _open = _from = null;
            if (PressIsOn(button)) ShutBy(button);
        };
        isOpen.AddValueChanged(menu, changed);
    }

    // dropped below input priority, so the click that ends the press still sees it
    private static void ShutBy(FrameworkElement button)
    {
        void Forget()
        {
            if (ReferenceEquals(_shutBy, button)) _shutBy = null;
        }
        _shutBy = button;
        button.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!button.IsMouseCaptured)
            {
                Forget();
                return;
            }
            MouseEventHandler? lost = null;
            lost = (_, _) =>
            {
                button.LostMouseCapture -= lost;
                button.Dispatcher.BeginInvoke(DispatcherPriority.Background, Forget);
            };
            button.LostMouseCapture += lost;
        });
    }

    private static bool Pressed(FrameworkElement button) =>
        button is ButtonBase { IsPressed: true } || (Mouse.LeftButton == MouseButtonState.Pressed && Under(button));

    internal static bool Under(FrameworkElement button)
    {
        // the pointer reads as 0,0 on a button that's out of its window, which would count as inside
        if (PresentationSource.FromVisual(button) is null) return false;
        var at = Mouse.GetPosition(button);
        return at.X >= 0 && at.Y >= 0 && at.X < button.ActualWidth && at.Y < button.ActualHeight;
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
