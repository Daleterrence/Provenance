using Provenance.Common.Credentials;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Serilog;

namespace Provenance.Common.Login;

public sealed partial class PolSession
{
    // ending a waiting pol.exe takes its 3s grace and up to 1s more, the rest is the check or the start unwinding
    private static readonly TimeSpan AheadCloseWait = TimeSpan.FromSeconds(8);

    private readonly object _aheadGate = new();
    private AheadLogin? _ahead;
    private Task _aheadGone = Task.CompletedTask;
    private bool _aheadClosed;

    // a waiting pol.exe holds the viewer's files, so one nobody's logged in with by now goes
    internal TimeSpan AheadLimit { get; init; } = TimeSpan.FromMinutes(3);

    // the first half of a login, run before the account's details are in. Only what that half uses decides whether a
    // login is for it: the account, the platform, the hook profile and the addon lists. Lean, the home slot and the
    // handle only go in the go file, so they can still differ
    private sealed class AheadLogin(string? profileId, ThirdPartyHook platform, SessionOptions options)
    {
        internal AheadProgress Progress { get; } = new();
        internal LoginRequest Request { get; } = new(new PolAccount("", ""), new SquareEnixAccount("", ""), "") { Options = options };
        internal CancellationTokenSource Cts { get; } = new();
        internal CancellationTokenSource Expiry { get; } = new();
        internal TaskCompletionSource Gone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LoginStage Stage { get; } = new();
        internal Task<int?> Result { get; set; } = null!;

        internal bool Matches(string? otherProfile, ThirdPartyHook otherPlatform, SessionOptions other) =>
            profileId == otherProfile && platform == otherPlatform && Request.Options.ThirdPartyHookProfile == other.ThirdPartyHookProfile
            && SameLists(Request.Options.WindowerLists, other.WindowerLists) && SameList(Request.Options.WindowerShared, other.WindowerShared);

        // once the first half has ended, whichever way, and nobody's taking it
        internal void Clear(Task result)
        {
            if (result.Exception is { } ex) Log.Debug(ex, "The early start ended badly");
            // nothing waiting on this is allowed to fail for it
            try
            {
                Stage.Dispose();
                Cts.Dispose();
                Expiry.Dispose();
            }
            catch (Exception e) { Log.Warning(e, "Ending the early start failed"); }
        }

        // taken for a login that can't use it
        internal void Abandon()
        {
            Expiry.Cancel();
            Cts.Cancel();
            try { Result.Wait(); }
            catch (AggregateException) { }
            Clear(Result);
        }

        private static bool SameLists(IReadOnlyDictionary<string, WindowerAddonList>? a, IReadOnlyDictionary<string, WindowerAddonList>? b)
        {
            if (a is null || b is null) return a is null && b is null;
            return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && SameList(kv.Value, other));
        }

        private static bool SameList(WindowerAddonList? a, WindowerAddonList? b)
        {
            if (a is null || b is null) return a is null && b is null;
            return a.UseGlobal == b.UseGlobal && a.Addons.SequenceEqual(b.Addons, StringComparer.Ordinal)
                   && a.Plugins.SequenceEqual(b.Plugins, StringComparer.Ordinal);
        }
    }

    // on the UI thread like ConnectAsync, the addon lists are read now. One already there for this account and these
    // options stays, any other goes first
    public void StartAhead(string? profileId, SessionOptions? options)
    {
        if (Volatile.Read(ref _played) || Pid is not null) return;
        var launch = ForLaunch(options);
        var platform = Platform;
        AheadLogin? replaced = null;
        lock (_aheadGate)
        {
            if (_aheadClosed) return;
            if (_ahead is { } current)
            {
                if (current.Matches(profileId, platform, launch)) return;
                replaced = current;
                _ahead = null;
                RetireLocked(current);
            }
            var ahead = new AheadLogin(profileId, platform, launch);
            var before = _aheadGone;
            ahead.Result = Task.Run(() => RunAhead(ahead, before));
            _ahead = ahead;
        }
        if (replaced is not null) Dismiss(replaced);
    }

    public static bool HeldByAnotherCopy(string profileId) => SessionRecords.HeldByAnotherCopy(profileId);

    // not waiting for it to end, the next login or start does
    public void DropAhead()
    {
        AheadLogin? dropped;
        lock (_aheadGate)
        {
            dropped = _ahead;
            _ahead = null;
            if (dropped is not null) RetireLocked(dropped);
        }
        if (dropped is not null) Dismiss(dropped);
    }

    internal bool AheadFinished { get { lock (_aheadGate) return _ahead?.Result.IsCompleted == true; } }

    internal bool HasAhead { get { lock (_aheadGate) return _ahead is not null; } }

    internal void WaitForAheadGone()
    {
        Task gone;
        lock (_aheadGate) gone = _aheadGone;
        gone.Wait();
    }

    private int? RunAhead(AheadLogin ahead, Task before)
    {
        try
        {
            before.Wait();
            if (ahead.Cts.IsCancellationRequested) return LoginCodes.Cancelled;
            return Prepare(ahead.Request with { Progress = ahead.Progress }, ahead.Cts.Token, ahead.Stage, ahead: true);
        }
        finally { ArmExpiry(ahead); }
    }

    private void ArmExpiry(AheadLogin ahead)
    {
        if (AheadLimit == Timeout.InfiniteTimeSpan) return;
        _ = Task.Delay(AheadLimit, ahead.Expiry.Token).ContinueWith(_ => ExpireAhead(ahead), CancellationToken.None,
                                                                    TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void ExpireAhead(AheadLogin ahead)
    {
        lock (_aheadGate)
        {
            if (_ahead != ahead) return;
            _ahead = null;
            RetireLocked(ahead);
        }
        Log.Information("No login used the early start, so it was ended");
        Dismiss(ahead);
    }

    // under the gate. What a login or another start waits for before it begins
    private Task RetireLocked(AheadLogin ahead) => _aheadGone = Task.WhenAll(_aheadGone, ahead.Gone.Task);

    // after RetireLocked and outside the gate. Gone is only set once the first half has ended and been cleared away
    private static void Dismiss(AheadLogin ahead)
    {
        Cancel(ahead);
        ahead.Result.ContinueWith(result =>
        {
            ahead.Clear(result);
            ahead.Gone.SetResult();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static void Cancel(AheadLogin ahead)
    {
        ahead.Expiry.Cancel();
        ahead.Cts.Cancel();
    }

    // the one for this login, or null once anything else has gone. A login never starts on top of one that's still ending
    private AheadLogin? TakeAhead(string? profileId, SessionOptions options)
    {
        AheadLogin? taken = null, dropped = null;
        Task gone;
        lock (_aheadGate)
        {
            var current = _ahead;
            _ahead = null;
            if (current is not null && current.Matches(profileId, Platform, options)) taken = current;
            else dropped = current;
            gone = dropped is null ? _aheadGone : RetireLocked(dropped);
        }
        if (dropped is not null) Dismiss(dropped);
        if (taken is null)
        {
            gone.Wait();
            return null;
        }
        taken.Expiry.Cancel();
        return taken;
    }

    // the first half's lines come out in order and the login's cancel reaches it, then the second half carries on
    private int FinishAhead(AheadLogin ahead, LoginRequest request, CancellationToken ct)
    {
        using var stage = ahead.Stage;
        try
        {
            using var link = ct.Register(ahead.Cts.Cancel);
            ahead.Progress.Attach(request.Progress);
            if (ahead.Result.GetAwaiter().GetResult() is { } code) return code;
            return Finish(request, ct, stage);
        }
        finally
        {
            ahead.Cts.Dispose();
            ahead.Expiry.Dispose();
        }
    }

    private void CloseAhead()
    {
        AheadLogin? dropped;
        Task gone;
        lock (_aheadGate)
        {
            _aheadClosed = true;
            dropped = _ahead;
            _ahead = null;
            if (dropped is not null) RetireLocked(dropped);
            gone = _aheadGone;
        }
        if (dropped is not null) Dismiss(dropped);
        gone.Wait(AheadCloseWait);
    }
}
