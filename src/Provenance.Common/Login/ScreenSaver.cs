using System.Diagnostics;
using Provenance.Common.Settings;
using Provenance.Common.Win32;
using Serilog;

namespace Provenance.Common.Login;

public static class ScreenSaver
{
    internal static string? AtStart { get; set; }

    public static void Note(ISettingsService settings) =>
        Note(settings, AnyPolRunning, Read, SessionRecords.PolEndedUnseen, Set);

    internal static bool AnyPolRunning()
    {
        var pols = Process.GetProcessesByName("pol");
        foreach (var p in pols) p.Dispose();
        return pols.Length > 0;
    }

    internal static void Note(ISettingsService settings, Func<bool> polRunning, Func<bool?> readWindows,
                              Func<bool>? endedUnseen = null, Action<bool>? set = null)
    {
        bool? on;
        var kept = settings.Current.ScreenSaverWasOn;
        if (polRunning())
        {
            on = settings.Current.ScreenSaverWasOn ?? readWindows();
        }
        else
        {
            on = readWindows();
            // pol.exe only ever switches it off, and one that crashed with nothing watching never put it back
            if (on == false && settings.Current.ScreenSaverWasOn == true && set is not null && endedUnseen?.Invoke() == true)
            {
                Log.Information("A pol.exe ended while no copy of Provenance was watching, so the screen saver's been put back on");
                set(true);
                on = true;
            }
            if (on is { } fresh)
            {
                // a start runs on a worker, and settings only change on the thread that owns them
                settings.OnOwnerThread(() =>
                {
                    if (settings.LoadProblem is null) settings.SaveChange(s => s.ScreenSaverWasOn = fresh);
                    else settings.Current.ScreenSaverWasOn = fresh;
                });
                kept = fresh;
            }
        }
        AtStart = on is { } v ? (v ? "1" : "0") : null;
        Saved = kept;
        if (on is null) Log.Warning("Could not read the screen saver setting, the hook reads it itself");
    }

    internal static bool? Saved { get; set; }

    internal static void PutBack() => PutBack(Saved, AnyPolRunning, Set);

    internal static bool PutBack(bool? wasOn, Func<bool> polRunning, Action<bool> set)
    {
        if (wasOn is not { } on || polRunning()) return false;
        set(on);
        return true;
    }

    internal static bool? Read() =>
        User32.SystemParametersInfoW(SpiGetScreenSaveActive, 0, out int on, 0) ? on != 0 : null;

    private static void Set(bool on) => User32.SystemParametersInfoW(SpiSetScreenSaveActive, on ? 1u : 0u, IntPtr.Zero, 0);

    private const uint SpiGetScreenSaveActive = 0x10;
    private const uint SpiSetScreenSaveActive = 0x11;
}
