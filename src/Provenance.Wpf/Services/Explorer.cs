using System.Diagnostics;
using System.IO;
using Provenance.Common;

namespace Provenance.Wpf.Services;

// the app runs elevated, so explorer.exe hands these to the user's own Explorer, same as links. Paths always go in
// quotes, explorer reads a comma in a bare one as another switch
internal static class Explorer
{
    // the full path, a bare name's looked for in the app folder first
    private static string Exe => AppPaths.ExplorerExe;

    internal static Func<ProcessStartInfo, Process?> StartForTest { get; set; } = Process.Start;

    public static void ShowFolder(string folder) => Start($"\"{folder.TrimEnd('\\')}\"");

    public static void ShowFile(string file) => Start($"/select,\"{file}\"");

    public static void OpenFile(string file) => Start($"\"{file}\"");

    // web links only, the shell will run a program, a network path or another protocol just the same
    public static void OpenLink(Uri link)
    {
        if (!link.IsAbsoluteUri || (link.Scheme != Uri.UriSchemeHttps && link.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Not a web link.", nameof(link));
        var psi = new ProcessStartInfo(Exe) { UseShellExecute = false };
        psi.ArgumentList.Add(link.AbsoluteUri.Replace(",", "%2C", StringComparison.Ordinal));
        using var p = StartForTest(psi);
    }

    private static void Start(string args)
    {
        using var p = StartForTest(new ProcessStartInfo(Exe, args) { UseShellExecute = false });
    }
}
