using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Provenance.Common.Security;
using Provenance.Launcher;
using Serilog;

namespace Provenance.Wpf.Services;

// The uninstaller (ProvenanceLauncher.exe, no admin) can't stop this elevated process, so it sets this copy's named
// event instead
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

    private sealed class Event : WaitHandle
    {
        public Event(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    private const int AlreadyExists = 183;
}
