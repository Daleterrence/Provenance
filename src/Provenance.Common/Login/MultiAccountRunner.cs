namespace Provenance.Common.Login;

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
