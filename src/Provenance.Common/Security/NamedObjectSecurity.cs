using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Provenance.Common.Win32;

namespace Provenance.Common.Security;

// the security for the app's named objects: the Windower handover's mapping and event, and the uninstaller's close event
public sealed class NamedObjectSecurity : IDisposable
{
    private IntPtr _descriptor;

    internal SecurityAttributes Attributes { get; }

    private NamedObjectSecurity(string sddl, Func<int, Exception> failed)
    {
        if (!Advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out _descriptor, IntPtr.Zero))
            throw failed(Marshal.GetLastPInvokeError());
        Attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = _descriptor };
    }

    // this user may set it and wait on it, even from medium integrity. Nothing else
    public static string CloseRequestSddl(string userSid) =>
        $"D:(A;;0x100002;;;{userSid})(A;;GA;;;SY)(A;;GA;;;BA)S:(ML;;NW;;;ME)";

    // elevated, only Administrators and SYSTEM, and nothing below high can open either. Not elevated (a dev run) the
    // user too, or the launch couldn't open its own
    public static string HandoverSddl(bool elevated, string userSid) =>
        elevated ? "O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)S:(ML;;NWNRNX;;;HI)" : $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{userSid})";

    public static NamedObjectSecurity ForCloseRequest()
    {
        using var me = WindowsIdentity.GetCurrent();
        return new NamedObjectSecurity(CloseRequestSddl(me.User!.Value), error => new Win32Exception(error));
    }

    public static NamedObjectSecurity ForHandover()
    {
        using var me = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
        return new NamedObjectSecurity(HandoverSddl(elevated, me.User!.Value),
            error => new InvalidOperationException($"Windower's handover security couldn't be made (error {error})."));
    }

    // the handle is invalid when it couldn't be made, and error 183 means it was there already
    public SafeWaitHandle CreateEvent(string name, bool manualReset, out int error)
    {
        var handle = Kernel32.CreateEventW(Attributes, manualReset, false, name);
        error = Marshal.GetLastPInvokeError();
        return handle;
    }

    public void Dispose()
    {
        if (_descriptor != IntPtr.Zero) Kernel32.LocalFree(_descriptor);
        _descriptor = IntPtr.Zero;
    }
}
