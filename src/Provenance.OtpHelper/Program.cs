namespace Provenance.OtpHelper;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (Request.Parse(args) is not { } request) return 2;
        try
        {
            ReplyPipe.Send(request.PipeName, OpRunner.Run(request));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return 3;
        }
    }
}
