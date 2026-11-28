using System.Globalization;
using System.Text.RegularExpressions;

namespace Provenance.OtpHelper;

internal sealed record Request(string PipeName, string OpPath, TimeSpan Limit, string Mode, string Item, string Account)
{
    internal const string OtpMode = "otp", AccountsMode = "accounts";

    private static readonly Regex PipeShape = new(@"^provenance-op-[0-9a-f]{32}$");
    private static readonly Regex AccountShape = new(@"^[A-Za-z0-9]{1,64}$");

    internal static Request? Parse(string[] args)
    {
        if (args.Length != 6 || !PipeShape.IsMatch(args[0])) return null;
        if (!Path.IsPathFullyQualified(args[1]) || !Path.GetFileName(args[1]).Equals("op.exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 300) return null;
        var mode = args[3];
        if (mode != OtpMode && mode != AccountsMode) return null;
        if (mode == OtpMode && (args[4].Length == 0 || args[4].TrimStart().StartsWith('-'))) return null;
        return new Request(args[0], args[1], TimeSpan.FromSeconds(seconds), mode, args[4], args[5]);
    }

    internal IReadOnlyList<string> OpArguments()
    {
        if (Mode == AccountsMode) return ["account", "list", "--format", "json"];
        List<string> arguments = ["item", "get", Item, "--otp"];
        if (AccountShape.IsMatch(Account)) arguments.AddRange(["--account", Account]);
        return arguments;
    }
}
