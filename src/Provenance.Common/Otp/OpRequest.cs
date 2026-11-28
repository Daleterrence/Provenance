using Provenance.OtpHelper;

namespace Provenance.Common.Otp;

internal sealed record OpRequest(string Mode, string Item, string Account)
{
    internal static OpRequest Accounts { get; } = new(OpCommand.AccountsMode, "", "");

    internal static OpRequest Otp(string item, string account) => new(OpCommand.OtpMode, item, account);

    internal IReadOnlyList<string> Args => OpCommand.Arguments(Mode, Item, Account);
}
