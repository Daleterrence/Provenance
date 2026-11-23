using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Provenance.Common.Settings;
using Provenance.Wpf.Controls;
using Provenance.Wpf.Controls.Behaviours;
using Provenance.Wpf.Services;
using Provenance.Wpf.ViewModels.Social;

namespace Provenance.Wpf.Views.Social;

public partial class GroupsWindow : AppWindow
{
    private SocialViewModel? Vm => DataContext as SocialViewModel;

    public GroupsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SocialViewModel old) Detach(old);
            if (e.NewValue is SocialViewModel vm) vm.GroupInviteReceived += OnInviteLanded;
            ReportVisible();
        };
        IsVisibleChanged += (_, _) => ReportVisible();
        StateChanged += (_, _) => ReportVisible();
        TaskbarFlash.StopWhenAppActivated(this);
        Closed += (_, _) =>
        {
            if (Vm is { } vm) Detach(vm);
        };
    }

    private void Detach(SocialViewModel vm)
    {
        vm.GroupsWindowVisible = false;
        vm.GroupInviteReceived -= OnInviteLanded;
    }

    private void OnInviteLanded(string who, int mid)
    {
        if (Vm?.Flashes(NoticeKind.GroupInvite) == true) TaskbarFlash.StartIfBehind(this);
    }

    private void ReportVisible()
    {
        if (Vm is { } vm) vm.GroupsWindowVisible = IsVisible && WindowState != WindowState.Minimized;
    }

    private void OnCreateGroup(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || !vm.CanEditGroups) return;
        if (!vm.Groups.CanCreate)
        {
            vm.Groups.ShowLimit();
            return;
        }
        string? name = null;
        SvgIcon.OpenWhile(sender, () => name = vm.Groups.AskName());
        if (!string.IsNullOrWhiteSpace(name)) vm.Groups.Create(name);
    }

    private void OnAcceptInvite(object sender, RoutedEventArgs e) => Answer(sender, true);

    private void OnDeclineInvite(object sender, RoutedEventArgs e) => Answer(sender, false);

    private void Answer(object sender, bool accept)
    {
        if (Vm is { } vm && (sender as FrameworkElement)?.DataContext is FriendItem invite) vm.Groups.AnswerInvite(invite, accept);
    }

    private void OnRowMenuButton(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;
        FrameworkElement? row = null;
        for (var p = VisualTreeHelper.GetParent(button); p != null && row is null; p = VisualTreeHelper.GetParent(p))
            if (p is FrameworkElement { ContextMenu: not null } el) row = el;
        if (row is null) return;
        Menus.Toggle(button, () => BuildRowMenu(row) ? row.ContextMenu : null);
    }

    private void OnRowMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement row || !BuildRowMenu(row))
        {
            e.Handled = true;
            return;
        }
        row.ContextMenu.ClearValue(ContextMenu.PlacementTargetProperty);
        row.ContextMenu.ClearValue(ContextMenu.PlacementProperty);
    }

    private bool BuildRowMenu(FrameworkElement row)
    {
        if (Vm is not { } vm || row.ContextMenu is not { } menu || vm.Groups.RowMenu(row.DataContext) is not { } actions)
            return false;
        Menus.Fill(menu, actions);
        return true;
    }
}
