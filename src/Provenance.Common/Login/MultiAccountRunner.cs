using Serilog;

namespace Provenance.Common.Login;

public sealed record MultiRunAccount(string ProfileId, string Name);

public sealed record MultiRunOptions(bool ContinueOnError, bool CloseWhenFinished);

public interface IMultiRunHost
{
    bool IsInGame(string profileId);
    Task<AccountFailure?> LogInAndPlayAsync(MultiRunAccount account, CancellationToken ct);
}

public sealed record MultiRunResult(IReadOnlyList<(MultiRunAccount Account, AccountFailure Failure)> Failures,
                                   bool Stopped, bool CloseApp, int Started = 0, int AlreadyInGame = 0);

public static class MultiAccountRunner
{
    public static async Task<MultiRunResult> RunAsync(IReadOnlyList<MultiRunAccount> accounts, MultiRunOptions options,
                                                      IMultiRunHost host, IProgress<string>? status, CancellationToken ct)
    {
        var list = accounts.ToArray();
        var failures = new List<(MultiRunAccount, AccountFailure)>();
        bool stopped = false;
        int started = 0, alreadyInGame = 0;
        for (int i = 0; i < list.Length; i++)
        {
            if (ct.IsCancellationRequested) { stopped = true; break; }
            var account = list[i];
            if (host.IsInGame(account.ProfileId))
            {
                Log.Information("Multi-account mode: {Account} is already in the game, so it isn't started again", account.ProfileId);
                alreadyInGame++;
                continue;
            }
            status?.Report($"Logging in {account.Name} ({i + 1} of {list.Length})...");
            AccountFailure? failure;
            try { failure = await host.LogInAndPlayAsync(account, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { stopped = true; break; }
            if (failure is null)
            {
                started++;
                continue;
            }
            failures.Add((account, failure));
            if (failure.Kind == AccountFailureKind.Install || !options.ContinueOnError) break;
        }
        // a run with nothing to start mustn't close the app, that's the only window to log anything else in from
        bool close = options.CloseWhenFinished && failures.Count == 0 && !stopped && started > 0;
        return new MultiRunResult(failures, stopped, close, started, alreadyInGame);
    }
}
