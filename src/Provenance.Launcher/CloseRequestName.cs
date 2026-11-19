using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Provenance.Launcher;

internal static class CloseRequestName
{
    public static string For(string appDir, int pid)
    {
        var dir = Path.TrimEndingDirectorySeparator(appDir).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dir)), 0, 8);
        return $@"Local\Provenance-close-{hash}-{pid}";
    }
}
