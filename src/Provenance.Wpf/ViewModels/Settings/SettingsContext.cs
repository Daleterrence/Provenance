namespace Provenance.Wpf.ViewModels.Settings;

// what's going on outside Settings that some of its pages depend on, read each time it's asked
internal sealed record SettingsContext(Func<bool> MultiRunning, Func<bool> LoggedIn, Func<bool> GameRunning);
