using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace Provenance.Wpf.Services;

// The uninstaller (ProvenanceLauncher.exe, no admin) can't stop this elevated process, so it sets this copy's named
// event instead. The name has to match the launcher's CloseRequestName
public static class CloseRequest
{
    private static WaitHandle? _event;   // open for the life of the process, the launcher reads it as "still running"

    public static string NameFor(string appDir, int pid)
    {
        var dir = Path.TrimEndingDirectorySeparator(appDir).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dir)), 0, 8);
        return $@"Local\Provenance-close-{hash}-{pid}";
    }

    public static void Listen(Action onRequest)
    {
        try
        {
            var name = NameFor(AppContext.BaseDirectory, Environment.ProcessId);
            using var me = WindowsIdentity.GetCurrent();
            // this user may set it and wait on it, even from medium integrity. Nothing else
            var sddl = $"D:(A;;0x100002;;;{me.User!.Value})(A;;GA;;;SY)(A;;GA;;;BA)S:(ML;;NW;;;ME)";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var sd, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            SafeWaitHandle handle;
            try
            {
                var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = sd };
                handle = CreateEventW(ref sa, true, false, name);
                int error = Marshal.GetLastWin32Error();
                if (handle.IsInvalid) throw new Win32Exception(error);
                if (error == AlreadyExists)
                {
                    // made by something else first, so it isn't mine to trust
                    handle.Dispose();
                    Log.Warning("{Name} already existed, so the uninstaller can't ask this copy to close", name);
                    return;
                }
            }
            finally { LocalFree(sd); }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateEventW(ref SecurityAttributes attributes, bool manualReset, bool initialState, string name);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
