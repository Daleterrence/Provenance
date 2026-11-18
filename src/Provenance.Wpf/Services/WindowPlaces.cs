using System.Windows;
using Provenance.Common.Settings;

namespace Provenance.Wpf.Services;

internal static class WindowPlaces
{
    internal static Func<Rect> ScreenArea { get; set; } = () =>
        new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

    // enough of the title bar on screen to grab it
    internal static bool OnScreen(WindowPlace p, Rect screen)
    {
        if (double.IsNaN(p.Left + p.Top + p.Width + p.Height) || double.IsInfinity(p.Left + p.Top + p.Width + p.Height) || p.Width <= 0 || p.Height <= 0)
            return false;
        var bar = Rect.Intersect(new Rect(p.Left, p.Top, p.Width, Math.Min(p.Height, 40)), screen);
        return !bar.IsEmpty && bar.Width >= 100 && bar.Height >= 30;
    }

    internal static bool Restore(ISettingsService settings, Window w, string key)
    {
        var screen = ScreenArea();
        if (!settings.Current.WindowPlaces.TryGetValue(key, out var p) || !OnScreen(p, screen)) return false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Width = Math.Min(Math.Max(p.Width, w.MinWidth), screen.Width);
        w.Height = Math.Min(Math.Max(p.Height, w.MinHeight), screen.Height);
        w.Left = p.Left;
        w.Top = p.Top;
        return true;
    }

    // saved as it closes or hides, from the normal bounds so a minimised or maximised one comes back as it was
    internal static void Track(ISettingsService settings, Window w, string key, Func<Rect, Rect>? shape = null)
    {
        void Save()
        {
            var b = w.RestoreBounds;
            if (!b.IsEmpty && shape is not null) b = shape(b);
            if (!w.IsLoaded || b.IsEmpty || b.Width <= 0 || b.Height <= 0) return;
            var place = new WindowPlace { Left = Math.Round(b.Left), Top = Math.Round(b.Top), Width = Math.Round(b.Width), Height = Math.Round(b.Height) };
            settings.SaveChange(s => s.WindowPlaces[key] = place);
        }
        w.Closing += (_, e) => { if (!e.Cancel) Save(); };
        w.IsVisibleChanged += (_, e) => { if (e.NewValue is false) Save(); };
    }
}
