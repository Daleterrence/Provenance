using System.Windows;
using Provenance.Wpf.ViewModels.Settings;

namespace Provenance.Wpf.Views.Settings;

public record SettingsPage(SettingsTab Tab, string Title, Func<SettingsViewModel, FrameworkElement> Create);

public sealed record GlyphPage(SettingsTab Tab, string Title, string Glyph, Func<SettingsViewModel, FrameworkElement> Create)
    : SettingsPage(Tab, Title, Create);

public sealed record MaterialPage(SettingsTab Tab, string Title, string Kind, Func<SettingsViewModel, FrameworkElement> Create)
    : SettingsPage(Tab, Title, Create);

// the nav list, in SettingsTab order
public static class SettingsPages
{
    public static IReadOnlyList<SettingsPage> All { get; } =
    [
        new GlyphPage(SettingsTab.General, "General", "settings", vm => new GeneralPage { DataContext = vm }),
        new GlyphPage(SettingsTab.WindowerAshita, "Windower/Ashita", "plugin", vm => new WindowerAshitaPage { DataContext = vm }),
        new GlyphPage(SettingsTab.Game, "Game", "gamepad", vm => new GamePage { DataContext = vm.Game }),
        new MaterialPage(SettingsTab.AddonsPlugins, "Addons and plugins", "PowerPlugOutline", vm => new AddonsPluginsView { DataContext = vm.Addons }),
        new GlyphPage(SettingsTab.MultiAccount, "Multi-account", "accounts", vm => new MultiAccountPage { DataContext = vm.Multi }),
        new GlyphPage(SettingsTab.Handles, "Handles", "person", vm => new HandlesPage { DataContext = vm.Handles }),
    ];
}
