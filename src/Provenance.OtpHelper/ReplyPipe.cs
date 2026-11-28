using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Provenance.OtpHelper;

internal sealed record Reply(
    [property: JsonPropertyName("exit")] int Exit,
    [property: JsonPropertyName("out")] string Out,
    [property: JsonPropertyName("err")] string Err,
    [property: JsonPropertyName("timedOut")] bool TimedOut);

internal static class ReplyPipe
{
    private const int ConnectMilliseconds = 10_000;

    internal static void Send(string pipeName, Reply reply)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
        pipe.Connect(ConnectMilliseconds);
        pipe.Write(JsonSerializer.SerializeToUtf8Bytes(reply));
    }
}
