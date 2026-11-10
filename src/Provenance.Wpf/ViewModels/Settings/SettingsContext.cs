namespace Provenance.Wpf.ViewModels.Settings;

internal sealed record SettingsContext(Func<bool> MultiRunning, Func<bool> LoggedIn, Func<bool> GameRunning);
