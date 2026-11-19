using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
using Provenance.Common.Security;
using Provenance.Launcher;
using Serilog;

namespace Provenance.Wpf.Services;

// the uninstaller (no admin) can't stop this elevated process
public static class CloseRequest
{
    private static WaitHandle? _event;   // open for the life of the process, the launcher reads it as "still running"

    public static void Listen(Action onRequest)
    {
        try
        {
            var name = CloseRequestName.For(AppContext.BaseDirectory, Environment.ProcessId);
            using var sa = NamedObjectSecurity.ForCloseRequest();
            var handle = sa.CreateEvent(name, true, out int error);
            if (handle.IsInvalid) throw new Win32Exception(error);
            if (error == AlreadyExists)
            {
                // made by something else first, so it isn't mine to trust
                handle.Dispose();
                Log.Warning("{Name} already existed, so the uninstaller can't ask this copy to close", name);
                return;
            }
            _event = new Event(handle);
            ThreadPool.RegisterWaitForSingleObject(_event, (_, _) =>
            {
                Log.Information("The uninstaller asked Provenance to close");
                onRequest();
            }, null, Timeout.Infinite, executeOnlyOnce: true);
        }
        catch (Exception ex) { Log.Warning(ex, "The uninstaller's close request could not be set up"); }
    }

    public static void AskOthers()
    {
        foreach (var p in Process.GetProcessesByName("Provenance"))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                try
                {
                    if (!EventWaitHandleAcl.TryOpenExisting(CloseRequestName.For(AppContext.BaseDirectory, p.Id), EventWaitHandleRights.Modify, out var asked))
                        continue;
                    using (asked) asked.Set();
                    Log.Information("Asked Provenance ({Pid}) to close for the update", p.Id);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    Log.Warning(ex, "Provenance ({Pid}) couldn't be asked to close", p.Id);
                }
            }
        }
    }

    private sealed class Event : WaitHandle
    {
        public Event(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    private const int AlreadyExists = 183;
}
