using Provenance.Common.Credentials;

namespace Provenance.Common.Login;

public sealed record LoginRequest(PolAccount Pol, SquareEnixAccount SquareEnix, string Otp)
{
    public string? ProfileId { get; init; }
    public IProgress<LaunchProgress>? Progress { get; init; }
    public Func<string?>? AskHandleName { get; init; }
    // a lean login asks this once the viewer's checked and before any pol.exe is used. False carries on as a full login
    public Func<bool>? BeforePol { get; init; }
    public SessionOptions Options { get; init; } = new(false, null);

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Otp = ***");
        return true;
    }
}
