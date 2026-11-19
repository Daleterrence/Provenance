using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Provenance.Launcher.Win32;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;

namespace Provenance.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool shift = (User32.GetAsyncKeyState(VkShift) & 0x8000) != 0;
        try
        {
            VelopackApp.Build()
                .SetAutoApplyOnStartup(false)
                .OnBeforeUninstallFastCallback(_ => CloseRunningCopies())
                .Run();
        }
        catch (Exception ex) { Note("Velopack start-up failed", ex); }

        // a hook mustn't start the app even if Run threw (UAC mid-uninstall)
        if (args.Length > 0 && args[0].StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase))
            return 0;

        // ShellExecute so Provenance.exe's manifest gets its UAC prompt, CreateProcess just fails with 740
        var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Provenance.exe")) { UseShellExecute = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (shift && !args.Contains(ShiftHeldSwitch, StringComparer.OrdinalIgnoreCase)) psi.ArgumentList.Add(ShiftHeldSwitch);
        try
        {
            Process.Start(psi)?.Dispose();
            return 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return 1;
        }
        catch (Exception ex)
        {
            Note("Could not start Provenance.exe", ex);
            Message("Provenance couldn't start. If this keeps happening, install Provenance again.", MbIconError);
            return 1;
        }
    }

    private static void CloseRunningCopies()
    {
        // Velopack kills this hook at 30s, so it all fits in 25 and an unanswered message can't hold it past that
        long until = Environment.TickCount64 + 25_000;
        TimeSpan Left() => TimeSpan.FromMilliseconds(Math.Max(0, until - Environment.TickCount64));
        var asked = new List<string>();
        bool unreachable = false;
        foreach (var p in Process.GetProcessesByName("Provenance"))
        {
            using (p)
            {
                var name = CloseRequestName.For(AppContext.BaseDirectory, p.Id);
                var h = Kernel32.OpenEventW(EventModifyState | Synchronize, false, name);
                if (h == IntPtr.Zero)
                {
                    // denied means mine but run as another account so it can't be asked, not found means another install's copy
                    if (Marshal.GetLastWin32Error() == ErrorAccessDenied) unreachable = true;
                    continue;
                }
                Kernel32.SetEvent(h);
                Kernel32.CloseHandle(h);
                asked.Add(name);
            }
        }
        if (!unreachable && (asked.Count == 0 || AllClosed(asked, TimeSpan.FromSeconds(12)))) return;
        var answered = new ManualResetEventSlim();
        new Thread(() =>
        {
            Message("Provenance is still running, so the uninstall can't remove all of its files. Close Provenance " +
                    "(if it's in the system tray, right-click its icon to exit), then press OK.", MbIconWarning);
            answered.Set();
        }) { IsBackground = true }.Start();
        while (Left() > TimeSpan.Zero)
        {
            if (!unreachable && AllClosed(asked, TimeSpan.Zero)) return;
            if (answered.Wait(250)) break;
        }
        AllClosed(asked, Left() < TimeSpan.FromSeconds(5) ? Left() : TimeSpan.FromSeconds(5));
    }

    private static bool AllClosed(List<string> names, TimeSpan limit)
    {
        long until = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        while (true)
        {
            bool open = false;
            foreach (var name in names)
            {
                var h = Kernel32.OpenEventW(Synchronize, false, name);
                if (h == IntPtr.Zero) continue;
                Kernel32.CloseHandle(h);
                open = true;
            }
            if (!open) return true;
            if (Environment.TickCount64 >= until) return false;
            Thread.Sleep(250);
        }
    }

    private static void Message(string text, uint icon) =>
        User32.MessageBoxW(IntPtr.Zero, text, "Provenance", MbOk | icon | MbTopmost | MbSetForeground);

    private static void Note(string what, Exception ex)
    {
        try
        {
            if (VelopackLocator.IsCurrentSet) VelopackLocator.Current.Log.Warn(ex, what);
        }
        catch { }
    }

    private const string ShiftHeldSwitch = "--shift-held";
    private const int ErrorCancelled = 1223, ErrorAccessDenied = 5, VkShift = 0x10;
    private const uint EventModifyState = 0x0002, Synchronize = 0x00100000;
    private const uint MbOk = 0x0, MbIconError = 0x10, MbIconWarning = 0x30, MbTopmost = 0x40000, MbSetForeground = 0x10000;
}
