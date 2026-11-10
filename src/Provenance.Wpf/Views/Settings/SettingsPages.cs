using System.Windows;
using Provenance.Wpf.ViewModels.Settings;

namespace Provenance.Wpf.Views.Settings;

public record GlyphIcon(string Glyph);

public record MaterialIcon(string Kind);

public record SettingsPage(SettingsTab Tab, string Title, object Icon, Func<SettingsViewModel, FrameworkElement> Create);

public static class SettingsPages
{
    public static IReadOnlyList<SettingsPage> All { get; } =
    [
        new SettingsPage(SettingsTab.General, "General", new GlyphIcon("settings"), vm => new GeneralPage { DataContext = vm }),
        new SettingsPage(SettingsTab.WindowerAshita, "Windower/Ashita", new GlyphIcon("plugin"), vm => new WindowerAshitaPage { DataContext = vm }),
        new SettingsPage(SettingsTab.Game, "Game", new GlyphIcon("gamepad"), vm => new GamePage { DataContext = vm.Game }),
        new SettingsPage(SettingsTab.AddonsPlugins, "Addons and plugins", new MaterialIcon("PowerPlugOutline"), vm => new AddonsPluginsView { DataContext = vm.Addons }),
        new SettingsPage(SettingsTab.MultiAccount, "Multi-account", new GlyphIcon("accounts"), vm => new MultiAccountPage { DataContext = vm.Multi }),
        new SettingsPage(SettingsTab.Handles, "Handles", new GlyphIcon("person"), vm => new HandlesPage { DataContext = vm.Handles }),
    ];
}
