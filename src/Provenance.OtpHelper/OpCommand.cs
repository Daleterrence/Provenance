using System.Text.RegularExpressions;

namespace Provenance.OtpHelper;

internal static class OpCommand
{
    internal const string OtpMode = "otp", AccountsMode = "accounts";
    internal const string AppUnlock = "OP_BIOMETRIC_UNLOCK_ENABLED";

    private static readonly Regex AccountShape = new(@"^[A-Za-z0-9]{1,64}$");

    internal static bool IsAccountId(string text) => AccountShape.IsMatch(text);

    internal static IReadOnlyList<string> Arguments(string mode, string item, string account)
    {
        if (mode == AccountsMode) return ["account", "list", "--format", "json"];
        List<string> arguments = ["item", "get", item, "--otp"];
        if (IsAccountId(account)) arguments.AddRange(["--account", account]);
        return arguments;
    }
}
