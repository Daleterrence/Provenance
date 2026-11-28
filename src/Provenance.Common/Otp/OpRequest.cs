using System.Text.RegularExpressions;

namespace Provenance.Common.Otp;

internal sealed record OpRequest(string Mode, string Item, string Account)
{
    internal const string OtpMode = "otp", AccountsMode = "accounts";

    private static readonly Regex AccountShape = new(@"^[A-Za-z0-9]{1,64}$");

    internal static OpRequest Accounts { get; } = new(AccountsMode, "", "");

    internal static OpRequest Otp(string item, string account) => new(OtpMode, item, account);

    internal static bool IsAccountId(string text) => AccountShape.IsMatch(text);

    internal IReadOnlyList<string> Args
    {
        get
        {
            if (Mode == AccountsMode) return ["account", "list", "--format", "json"];
            List<string> args = ["item", "get", Item, "--otp"];
            if (IsAccountId(Account)) args.AddRange(["--account", Account]);
            return args;
        }
    }
}
