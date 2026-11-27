using System.Security.Principal;
using Provenance.OtpProbe.Win32;

namespace Provenance.OtpProbe;

internal static class Program
{
    private const string Usage = "usage: ProvenanceOtpProbe <parent|token> <item> [account]";
    private const uint WaitLimitMilliseconds = 120_000;

    private static int Main(string[] args)
    {
        if (args.Length < 2 || args[1].Contains('"') || args[1].StartsWith('-')) return Fail(Usage);
        var start = args[0] switch
        {
            "parent" => (Func<string, string, IntPtr>)UnelevatedStart.UnderExplorerParent,
            "token" => UnelevatedStart.WithExplorerToken,
            _ => null
        };
        if (start is null) return Fail(Usage);

        var helper = Path.Combine(AppContext.BaseDirectory, "ProvenanceOp.exe");
        var op = Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "Provenance", "tools", "op.exe");
        if (!File.Exists(helper)) return Fail("ProvenanceOp.exe is not next to this program.");
        if (!File.Exists(op)) return Fail("op.exe is not in " + op);

        var account = args.Length > 2 ? args[2] : "-";
        var resultPath = Path.Combine(Path.GetTempPath(), "provenance-op-probe-" + Guid.NewGuid().ToString("N") + ".txt");
        var commandLine = string.Join(' ', new[] { helper, op, args[1], account, resultPath }.Select(a => '"' + a + '"'));

        Console.WriteLine("probe elevated: " + IsElevated());
        Console.WriteLine("variant: " + args[0]);
        var process = start(commandLine, AppContext.BaseDirectory);
        Console.WriteLine("helper started, waiting for 1Password's prompt...");
        var waited = Kernel32.WaitForSingleObject(process, WaitLimitMilliseconds);
        Kernel32.GetExitCodeProcess(process, out var exit);
        Kernel32.CloseHandle(process);

        Console.WriteLine(waited == 0 ? "helper exit: " + exit : "helper did not finish in time");
        if (!File.Exists(resultPath)) return Fail("the helper wrote no result.");
        Console.WriteLine(File.ReadAllText(resultPath));
        File.Delete(resultPath);
        return 0;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}
