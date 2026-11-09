namespace Provenance.Common.Otp;

// what's known about each command-line tool. "op" and "bw" are the file names, the source ids are what's saved with an account
public static class ToolFacts
{
    public const string OnePassword = "op", Bitwarden = "bw";

    private sealed record Fact(string Tool, string Source, string Name, Version Oldest);

    // Oldest is the version this was built and tested against. An older one, even a genuine one, never goes in or gets used
    private static readonly Fact[] Table =
    [
        new(OnePassword, OtpSources.OnePassword, "1Password", new(2, 41, 1)),
        new(Bitwarden, OtpSources.Bitwarden, "Bitwarden", new(2026, 10, 1)),
    ];

    public static string Name(string tool) => Table.Single(f => f.Tool == tool).Name;

    public static string? ForSource(string source) => Table.FirstOrDefault(f => f.Source == source)?.Tool;

    internal static Version Oldest(string tool) => Table.Single(f => f.Tool == tool).Oldest;

    internal static string? SourceName(string source) => Table.FirstOrDefault(f => f.Source == source)?.Name;

    internal static string LockName(string tool) => "cli-tool-" + tool;
}
