using System.Runtime.InteropServices;
using System.Text;

namespace Provenance.Common.Win32;

[ComImport, Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLink { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
internal interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, uint flags);
    void GetIDList(out IntPtr pidl);
    void SetIDList(IntPtr pidl);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
    void GetHotkey(out short hotkey);
    void SetHotkey(short hotkey);
    void GetShowCmd(out int show);
    void SetShowCmd(int show);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int icon);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string rel, uint reserved);
    void Resolve(IntPtr hwnd, uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}
