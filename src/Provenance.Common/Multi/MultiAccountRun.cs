namespace Provenance.Common.Multi;

// DetailsUnreadable is the hook failing to read the saved entries and Unfinished a login that stopped short, neither one
// a POL refusal
public static class SignInCodes
{
    public const int AuthFailed = -2, NoPolHandle = -3, LauncherFailed = -5, Stalled = -6,
                     HandleRejected = -7, Cancelled = -8, AccountBlocked = -10, DetailsUnreadable = -11, Unfinished = -12;
}

public enum AccountFailureKind { Account, Credential, AccountBlocked, Install }

public sealed record AccountFailure(AccountFailureKind Kind, string Text);

public static class AccountFailures
{
    // POL answers -5311/-5312 for a wrong SE password and a wrong one-time password alike, so with OTP on it can only
    // count as an Account failure
    public static AccountFailureKind Classify(int code, int? refusalReason, bool usesOtp)
    {
        if (code == SignInCodes.AccountBlocked) return AccountFailureKind.AccountBlocked;
        if (code == SignInCodes.DetailsUnreadable) return AccountFailureKind.Credential;   // off until the account's edited
        if (code == SignInCodes.LauncherFailed) return AccountFailureKind.Install;
        if (code != SignInCodes.AuthFailed) return AccountFailureKind.Account;
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

public sealed record MultiRunAccount(string ProfileId, string Name);

public sealed record MultiRunOptions(bool ContinueOnError, bool CloseWhenFinished);

public interface IMultiRunHost
{
    bool IsInGame(string profileId);
    // null means it's in the game and its session was released
    Task<AccountFailure?> SignInAndPlayAsync(MultiRunAccount account, CancellationToken ct);
}

public sealed record MultiRunResult(IReadOnlyList<(MultiRunAccount Account, AccountFailure Failure)> Failures,
                                   bool Stopped, bool CloseApp);

public static class MultiAccountRunner
{
    public static async Task<MultiRunResult> RunAsync(IReadOnlyList<MultiRunAccount> accounts, MultiRunOptions options,
                                                      IMultiRunHost host, IProgress<string>? status, CancellationToken ct)
    {
        var list = accounts.ToArray();   // in case the caller changes theirs mid-run
        var failures = new List<(MultiRunAccount, AccountFailure)>();
        bool stopped = false;
        for (int i = 0; i < list.Length; i++)
        {
            if (ct.IsCancellationRequested) { stopped = true; break; }
            var account = list[i];
            if (host.IsInGame(account.ProfileId)) continue;
            status?.Report($"Logging in {account.Name} ({i + 1} of {list.Length})...");
            AccountFailure? failure;
            try { failure = await host.SignInAndPlayAsync(account, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { stopped = true; break; }
            if (failure is null) continue;
            failures.Add((account, failure));
            if (failure.Kind == AccountFailureKind.Install || !options.ContinueOnError) break;
        }
        return new MultiRunResult(failures, stopped, options.CloseWhenFinished && failures.Count == 0 && !stopped);
    }
}
