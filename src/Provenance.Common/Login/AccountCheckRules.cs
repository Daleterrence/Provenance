namespace Provenance.Common.Login;

public enum AccountCheckVerdict { Accepted, Refused, Locked, Cancelled, CouldNot }

public static class AccountCheckRules
{
    public const string VerifyingText = "Verifying your account information...";

    public const int AskAt = 2;

    public static AccountCheckVerdict Judge(int code, int? refusal)
    {
        if (code is 0 or LoginCodes.NoPolHandle) return AccountCheckVerdict.Accepted;
        if (code == LoginCodes.Cancelled) return AccountCheckVerdict.Cancelled;
        if (code != LoginCodes.AuthFailed) return AccountCheckVerdict.CouldNot;
        return refusal switch
        {
            -205 => AccountCheckVerdict.Locked,
            null or -202 or -203 or -5311 or -5312 => AccountCheckVerdict.Refused,
            _ => AccountCheckVerdict.CouldNot,
        };
    }

    public static string? LockoutQuestion(int refused) => refused < AskAt ? null :
        $"PlayOnline has refused the password for this PlayOnline ID {refused} times in a row. Three wrong passwords in a row lock an account for a while, so the next wrong one could do that.\n\nLog in to check it now?";
}
