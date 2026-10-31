using System.Diagnostics;
using System.IO;

namespace Provenance.Wpf.Services;

// the app runs elevated, so explorer.exe hands these to the user's own Explorer, same as links. Paths always go in
// quotes, explorer reads a comma in a bare one as another switch
internal static class Explorer
{
    private static string Exe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    public static void ShowFolder(string folder) => Start($"\"{folder.TrimEnd('\\')}\"");

    public static void ShowFile(string file) => Start($"/select,\"{file}\"");

    private static void Start(string args)
    {
        using var p = Process.Start(new ProcessStartInfo(Exe, args) { UseShellExecute = false });
    }
}
