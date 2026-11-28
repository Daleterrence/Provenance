using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Provenance.Common.Win32;
using Serilog;

namespace Provenance.Common.Otp;

internal static class OnePasswordHelper
{
    internal const string FileName = "Provenance 1Password Helper.exe";

    private const int MaxReplyBytes = 262_144;
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(10), Grace = TimeSpan.FromMilliseconds(500);

    internal static string Exe => Path.Combine(AppContext.BaseDirectory, FileName);

    internal static async Task<ToolProcess.RunResult> RunAsync(string op, OpRequest request, IDictionary<string, string> env, TimeSpan limit,
                                                               CancellationToken ct)
    {
        if (Start(op, request, limit) is not { } session) return await ToolProcess.RunAsync(op, request.Args, env, limit, ct);
        using (session) return await session.ReplyAsync(op, limit, ct);
    }

    private static Session? Start(string op, OpRequest request, TimeSpan limit)
    {
        if (!File.Exists(Exe))
        {
            Log.Information("{Helper} isn't next to Provenance, so op runs from Provenance itself", FileName);
            return null;
        }
        ShellToken? shell = null;
        NamedPipeServerStream? server = null;
        LaunchedProcess? launched = null;
        try
        {
            shell = ShellToken.Open();
            var pipeName = "provenance-op-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            server = CreateServer(pipeName, shell.User);
            var seconds = ((int)Math.Ceiling(limit.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            launched = shell.Start(CommandLine.Join([Exe, pipeName, op, seconds, request.Mode, request.Item, request.Account]), AppContext.BaseDirectory);
            return new Session(shell, server, launched, Process.GetProcessById(launched.Id));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warning(ex, "Could not start {Helper}, so op runs from Provenance itself", FileName);
            launched?.Dispose();
            server?.Dispose();
            shell?.Dispose();
            return null;
        }
    }

    private static NamedPipeServerStream CreateServer(string name, SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                                               PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    private sealed class Session(ShellToken shell, NamedPipeServerStream server, LaunchedProcess launched, Process process) : IDisposable
    {
        internal async Task<ToolProcess.RunResult> ReplyAsync(string op, TimeSpan limit, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(limit + Slack);
            try { return Parse(await ReadAsync(deadline.Token), op, limit); }
            catch (OperationCanceledException)
            {
                Stop();
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException($"{Path.GetFileNameWithoutExtension(op)} didn't answer within {limit.TotalSeconds:0} seconds.");
            }
        }

        public void Dispose()
        {
            process.Dispose();
            server.Dispose();
            launched.Dispose();
            shell.Dispose();
        }

        private async Task<byte[]> ReadAsync(CancellationToken token)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                var exited = process.WaitForExitAsync(stop.Token);
                while (true)
                {
                    var connect = server.WaitForConnectionAsync(stop.Token);
                    if (await Task.WhenAny(connect, exited) == exited && !connect.IsCompleted)
                    {
                        await Task.WhenAny(connect, Task.Delay(Grace, token));
                        if (!connect.IsCompleted) throw new InvalidOperationException($"{FileName} ended without an answer.");
                    }
                    await connect;
                    if (ClientIsHelper()) break;
                    server.Disconnect();
                }
                return await ReadAllAsync(token);
            }
            finally { stop.Cancel(); }
        }

        private async Task<byte[]> ReadAllAsync(CancellationToken token)
        {
            using var reply = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await server.ReadAsync(chunk, token)) > 0)
            {
                if (reply.Length + read > MaxReplyBytes) throw new InvalidDataException($"{FileName}'s answer was too long.");
                reply.Write(chunk, 0, read);
            }
            return reply.ToArray();
        }

        private bool ClientIsHelper() =>
            Kernel32.GetNamedPipeClientProcessId(server.SafePipeHandle, out var client) && client == (uint)launched.Id;

        private void Stop()
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        }

        private static ToolProcess.RunResult Parse(byte[] reply, string op, TimeSpan limit)
        {
            using var document = JsonDocument.Parse(reply);
            var root = document.RootElement;
            if (root.TryGetProperty("timedOut", out var timedOut) && timedOut.ValueKind == JsonValueKind.True)
                throw new TimeoutException($"{Path.GetFileNameWithoutExtension(op)} didn't answer within {limit.TotalSeconds:0} seconds.");
            return new ToolProcess.RunResult(root.GetProperty("exit").GetInt32(), root.GetProperty("out").GetString() ?? "",
                                             root.GetProperty("err").GetString() ?? "");
        }
    }
}
