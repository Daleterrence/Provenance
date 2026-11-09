using Provenance.Common.Credentials;

namespace Provenance.Common.Login;

public sealed record LoginRequest(PolAccount Pol, SquareEnixAccount SquareEnix, string Otp)
{
    public string? ProfileId { get; init; }
    public IProgress<LaunchProgress>? Progress { get; init; }
    public Func<string?>? AskHandleName { get; init; }
    public SessionOptions Options { get; init; } = new(false, null);

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Otp = ***");
        return true;
    }
}
