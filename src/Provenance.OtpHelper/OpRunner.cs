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
        // the user's own OP_ settings can send op to a service account or Connect and never the 1Password app
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("OP_", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(name);
        start.Environment[OpCommand.AppUnlock] = "true";
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
