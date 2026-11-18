using System.Windows;
using Provenance.Wpf.ViewModels.Settings;

namespace Provenance.Wpf.Views.Settings;

public record GlyphIcon(string Glyph);

public record MaterialIcon(string Kind);

public record SettingsPage(SettingsTab Tab, string Group, string Title, object Icon, Func<SettingsViewModel, FrameworkElement> Create);

public static class SettingsPages
{
    public const string GameTitle = "FINAL FANTASY XI";

    public static IReadOnlyList<SettingsPage> All { get; } =
    [
        new SettingsPage(SettingsTab.General, "Provenance", "General", new GlyphIcon("settings"), vm => new GeneralPage { DataContext = vm }),
        new SettingsPage(SettingsTab.Notifications, "Provenance", "Notifications", new MaterialIcon("BellOutline"), vm => new NotificationsPage { DataContext = vm.Notifications }),
        new SettingsPage(SettingsTab.Game, "Game", GameTitle, new GlyphIcon("crystal"), vm => new GamePage { DataContext = vm.Game }),
        new SettingsPage(SettingsTab.WindowerAshita, "Game", "Windower/Ashita", new GlyphIcon("plugin"), vm => new WindowerAshitaPage { DataContext = vm }),
        new SettingsPage(SettingsTab.AddonsPlugins, "Game", "Addons and plugins", new MaterialIcon("PowerPlugOutline"), vm => new AddonsPluginsView { DataContext = vm.Addons }),
        new SettingsPage(SettingsTab.Backups, "Game", "Backups", new MaterialIcon("BackupRestore"), vm => new BackupsPage { DataContext = vm.Backups }),
        new SettingsPage(SettingsTab.MultiAccount, "Accounts", "Multi-account", new GlyphIcon("accounts"), vm => new MultiAccountPage { DataContext = vm.Multi }),
        new SettingsPage(SettingsTab.Handles, "Accounts", "Handles", new GlyphIcon("person"), vm => new HandlesPage { DataContext = vm.Handles }),
    ];
}
