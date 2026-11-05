namespace Provenance.Common.Login;

public enum AccountFailureKind { Account, Credential, AccountBlocked, Install }

public sealed record AccountFailure(AccountFailureKind Kind, string Text);

public static class AccountFailures
{
    // POL answers -5311/-5312 for a wrong SE password and a wrong one-time password alike, so with OTP on it can only
    // count as an Account failure
    public static AccountFailureKind Classify(int code, int? refusalReason, bool usesOtp)
    {
        if (code == LoginCodes.AccountBlocked) return AccountFailureKind.AccountBlocked;
        if (code == LoginCodes.DetailsUnreadable) return AccountFailureKind.Credential;   // off until the account's edited
        if (code == LoginCodes.LauncherFailed) return AccountFailureKind.Install;
        if (code != LoginCodes.AuthFailed) return AccountFailureKind.Account;
        return refusalReason switch
        {
            -202 or -203 or -205 => AccountFailureKind.Credential,
            -5311 or -5312 => usesOtp ? AccountFailureKind.Account : AccountFailureKind.Credential,
            -5310 => AccountFailureKind.AccountBlocked,
            -204 => AccountFailureKind.Install,
            _ => AccountFailureKind.Account,
        };
    }
}
