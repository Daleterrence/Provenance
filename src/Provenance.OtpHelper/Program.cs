using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Provenance.OtpHelper;

internal static class Program
{
    private static readonly TimeSpan OpLimit = TimeSpan.FromSeconds(90);
    private static readonly Regex CodeShape = new(@"^\d{6,8}$");
    private static readonly Regex AccountShape = new(@"^[A-Za-z0-9]{1,64}$");

    private static int Main(string[] args)
    {
        if (args.Length != 4 || args[1].StartsWith('-')) return 2;
        var (op, item, account, resultPath) = (args[0], args[1], args[2], args[3]);
        File.WriteAllText(resultPath, Report(op, item, account));
        return 0;
    }

    private static string Report(string op, string item, string account)
    {
        var report = new StringBuilder();
        report.AppendLine("elevated=" + IsElevated());
        try
        {
            var run = Run(op, item, account);
            report.AppendLine("exit=" + run.Exit);
            report.AppendLine("code=" + (CodeShape.IsMatch(run.Out.Trim()) ? "looks like a code" : "no code"));
            report.AppendLine("stderr=" + run.Err.Trim());
        }
        catch (Exception ex)
        {
            report.AppendLine("failed=" + ex.GetType().Name + ": " + ex.Message);
        }
        return report.ToString();
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static (int Exit, string Out, string Err) Run(string op, string item, string account)
    {
        var start = new ProcessStartInfo(op)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["OP_BIOMETRIC_UNLOCK_ENABLED"] = "true";
        foreach (var arg in new[] { "item", "get", item, "--otp" }) start.ArgumentList.Add(arg);
        if (AccountShape.IsMatch(account))
        {
            start.ArgumentList.Add("--account");
            start.ArgumentList.Add(account);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("op did not start.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(OpLimit))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("op did not answer in time.");
        }
        return (process.ExitCode, output.Result, error.Result);
    }
}
