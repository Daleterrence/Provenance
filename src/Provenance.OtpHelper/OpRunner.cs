using System.ComponentModel;
using System.Diagnostics;

namespace Provenance.OtpHelper;

internal static class OpRunner
{
    private static readonly TimeSpan OutputWait = TimeSpan.FromSeconds(5);

    internal static Reply Run(Request request)
    {
        var start = new ProcessStartInfo(request.OpPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["OP_BIOMETRIC_UNLOCK_ENABLED"] = "true";
        foreach (var argument in request.OpArguments()) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("op did not start.");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(request.Limit))
            {
                process.Kill(entireProcessTree: true);
                return new Reply(-1, "", "", true);
            }
            Task.WaitAll([output, error], OutputWait);
            return new Reply(process.ExitCode, output.IsCompletedSuccessfully ? output.Result : "", error.IsCompletedSuccessfully ? error.Result : "", false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            return new Reply(-1, "", ex.Message, false);
        }
    }
}
