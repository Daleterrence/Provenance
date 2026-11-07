using System.Runtime.InteropServices;
using System.Text;

namespace Provenance.Common.Win32;

internal static class Msi
{
    [DllImport("msi.dll")] internal static extern uint MsiCloseHandle(IntPtr handle);
    [DllImport("msi.dll")] internal static extern IntPtr MsiCreateRecord(uint fields);
    [DllImport("msi.dll")] internal static extern uint MsiDatabaseCommit(IntPtr db);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] internal static extern uint MsiDatabaseOpenViewW(IntPtr db, string query, out IntPtr view);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] internal static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr db);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] internal static extern int MsiQueryProductStateW(string productCode);
    [DllImport("msi.dll")] internal static extern uint MsiRecordGetFieldCount(IntPtr record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] internal static extern uint MsiRecordGetStringW(IntPtr record, uint field, StringBuilder value, ref uint len);
    [DllImport("msi.dll")] internal static extern uint MsiRecordReadStream(IntPtr record, uint field, byte[] buf, ref uint len);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] internal static extern uint MsiRecordSetStringW(IntPtr record, uint field, string value);
    [DllImport("msi.dll")] internal static extern uint MsiViewExecute(IntPtr view, IntPtr record);
    [DllImport("msi.dll")] internal static extern uint MsiViewFetch(IntPtr view, out IntPtr record);
    [DllImport("msi.dll")] internal static extern uint MsiViewModify(IntPtr view, int mode, IntPtr record);
}
