using System.Text.RegularExpressions;
using Provenance.Common.Credentials;
using Serilog;

namespace Provenance.Common.Otp;

public enum OtpFix { TryAgain, Install, OpenOnePasswordSettings, TypeInstead, MasterPassword }

public sealed record OtpFetchResult(string? Code, string? Problem, OtpFix Fix = OtpFix.TryAgain, string? Tool = null)
{
    internal string Outcome { get; init; } = "";

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Code = ***");
        return true;
    }
}

public static class OtpSources
{
    public const string Bitwarden = "bitwarden", OnePassword = "1password", KeePassXC = "keepassxc";
    internal static readonly Regex SixDigits = new(@"^\d{6}$");

    public const string DashText = "Provenance can't use an item name that starts with a dash.";

    public static string Name(string source) => source == KeePassXC ? "KeePassXC" : ToolFacts.SourceName(source) ?? "";

    public static (string Hint, string Help) Describe(string source) => source switch
    {
        Bitwarden => ("Item name or ID in Bitwarden",
                      "Provenance gets the code from the Bitwarden command-line tool. Bitwarden only gives codes to Premium accounts."),
        OnePassword => ("Item name or ID in 1Password",
                        "Provenance gets the code from the 1Password app. In 1Password, turn on Integrate with 1Password CLI (Settings > Developer)."),
        KeePassXC => ("Entry URL in KeePassXC",
                      "Provenance gets the code from KeePassXC, using the URL saved in the entry. Turn on Enable browser integration in KeePassXC (Tools > Settings > Browser Integration)."),
        _ => ("", ""),
    };

    public static async Task<OtpFetchResult> FetchAsync(string source, string item, string account, string squareEnixId, string? bitwardenPassword,
                                                        CancellationToken ct)
    {
        OtpFetchResult r;
        try
        {
            r = source switch
            {
                OnePassword => await Otp.OnePassword.FetchAsync(item, account, ct),
                Bitwarden => await Otp.Bitwarden.FetchAsync(item, bitwardenPassword, ct),
                KeePassXC => await KeePassXc.FetchAsync(item, squareEnixId, ct),
                _ => Problem("no manager", "This account doesn't have a password manager chosen.", OtpFix.TypeInstead),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { r = Problem("cancelled", "Cancelled."); }
        catch (TimeoutException ex) { r = Problem("timed out", source == KeePassXC ? ex.Message : $"{Name(source)} didn't answer."); }
        catch (CredentialKeyUnavailableException ex) { r = Problem("key file unavailable", ex.Message, OtpFix.TypeInstead); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading the one-time password from {Source} failed", source);
            r = Problem("failed", $"{Name(source)} didn't give the code.");
        }
        Log.Information("One-time password from {Source}: {Outcome}", Name(source), r.Code is null ? r.Outcome : "got the code");
        return r;
    }

    internal static OtpFetchResult Problem(string outcome, string text, OtpFix fix = OtpFix.TryAgain, string? tool = null) =>
        new(null, text, fix, tool) { Outcome = outcome };

    internal static OtpFetchResult ToolProblem(string tool, ToolFind found) =>
        Problem(found.State switch { FindState.CantCheck => "couldn't check", FindState.TooOld => "too old", _ => "not installed" },
                ToolTexts.Problem(found.State, tool), OtpFix.Install, tool);

    // a leading dash would make the item an option, and settings.json is the user's, so this is checked at the run too
    public static bool StartsWithDash(string item) => item.TrimStart().StartsWith('-');

    private static readonly Regex Colours = new(@"\x1b\[[0-9;]*m"), LogStamp = new(@"^\[ERROR\]\s+\S+\s+\S+\s+"), Address = new(@"\S+@\S+"),
                                  Url = new(@"https?://\S+"), Quoted = new("\"[^\"]+\"|(?<![\\w'])'[^']+'(?![\\w'])"),
                                  Folder = new(@"(?<!\w)(?:[A-Za-z]:[\\/]|\\\\)(?:(?![A-Za-z]:[\\/]|\x20E(?:NOENT|ACCES|PERM|BUSY|NOTDIR|INVAL|MFILE|NOMEM)\s*$)[^""<>|?*:\r\n])*"),
                                  Host = new(@"(?<![\w-])(?:[\w-]+\.)+[\w-]+(?::\d+)?"), Lookup = new(@"\b(getaddrinfo \w+ |Host: )[\w.-]+"),
                                  Port = new(@"(?<![\w.:-])(?=[\w-]*[A-Za-z])[\w-]+:\d{2,5}(?![\w.:])"),
                                  Ip6 = new(@"(?<![\w.])(?<![0-9A-Fa-f:]:)\[?(?=[0-9A-Fa-f:]*::|(?:[0-9A-Fa-f]{1,4}:){7})[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,8}(?:%[^\s\]]+)?\]?(?::\d+)?(?![\w:])");

    internal static string ErrorLine(string err)
    {
        var line = Colours.Replace(err, "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return LogStamp.Replace(line, "");
    }

    // what the tool itself said, for the log: its first line without the item's name, servers, paths or anything it quotes
    internal static string Said(string err, string item)
    {
        var line = ErrorLine(err);
        if (item.Trim() is { Length: > 0 } name)
            line = Regex.Replace(line, $@"(?<!\w){Regex.Escape(name)}(?!\w)", "<item>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        line = Quoted.Replace(line, m => m.Value[1..^1] == "<item>" ? m.Value : $"{m.Value[0]}<name>{m.Value[0]}");
        line = Address.Replace(Url.Replace(line, "<address>"), "<email>");
        // a path can have spaces in it, so it runs to something a path can't hold, or to the error code Node ends the line with
        line = Folder.Replace(line, m => "<path>" + m.Value[m.Value.TrimEnd().Length..]);
        line = Lookup.Replace(Ip6.Replace(line, "<address>"), "$1<address>");
        line = Port.Replace(Host.Replace(line, "<address>"), "<address>");
        return line.Length > 160 ? line[..160] : line;
    }

    internal static string AndSaid(string outcome, string err, string item) => Said(err, item) is { Length: > 0 } said ? $"{outcome}: {said}" : outcome;

    internal static OtpFetchResult CodeFrom(ToolProcess.RunResult r, Func<string, (string Outcome, string Text, OtpFix Fix)> problem)
    {
        var code = r.Out.Trim();
        if (r.Exit == 0 && SixDigits.IsMatch(code)) return new OtpFetchResult(code, null);
        var (outcome, text, fix) = problem(r.Err);
        return Problem($"{outcome} (exit {r.Exit})", text, fix);
    }
}
