using Provenance.Common.Game;
using Provenance.Common.Hook;

namespace Provenance.Common.Social;

public enum PreLaunchResult { Play, Stop, ShowHandles }

// the viewer's last step before a game starts (app.dll 0x101def83): unlinked FFXI Content IDs go on the active handle.
// It only asks when there's more than one handle
public sealed class PreLaunchLink
{
    public const string Title = "Link Content ID";
    public const string AskText = "There is a Content ID that is not linked to a handle.\nLink Content ID to the current active handle \"{0}\"?";
    public const string FullText = "The handle \"{0}\" is already linked to 8 Content IDs.\nDisplay Handle List?";
    public const string ShowListText = "Display Handle List\nand link Content ID?";
    public const string SavingText = "Updating Content ID list...";
    public const string PageGaveUpText = "Your last handle change hasn't finished, so your characters couldn't be linked. Try again in a moment.";
    public const string AlreadyLinkedText = "Not saved: one of the characters is already linked to a handle.";

    // Fit is as many as the active handle has room for, in index order like the viewer's scan
    public sealed record Plan(PolHandle Active, IReadOnlyList<PolContentId> Unlinked, IReadOnlyList<PolContentId> Fit, bool Ask);

    public static Plan? For(PolSocialSnapshot snap)
    {
        if (snap.Handles.FirstOrDefault(h => h.Active) is not { } active) return null;
        var unlinked = snap.ContentIds.Where(c => c.Type == HandleList.Ffxi && c.Handle < 0).OrderBy(c => c.Index).ToList();
        if (unlinked.Count == 0) return null;
        int room = Math.Max(0, HandleList.MaxContentIds - active.ContentIds.Count);
        return new Plan(active, unlinked, unlinked.Take(room).ToList(), snap.Handles.Count > 1);
    }

    // the game can't use a character that isn't linked to a handle, so a link that didn't work stops it starting
    public static string FailedText(string failure) => $"{failure}\n\nFINAL FANTASY XI wasn't started.";

    public required Func<int?> Pid { get; init; }
    public required Func<string, bool> Ask { get; init; }
    public required Action<string> Failed { get; init; }
    public Action<string>? Saying { get; init; }
    // the Handles page's own progress text while its change is going, there's only one handle request at a time
    public Func<string?> PageWaiting { get; init; } = () => null;
    public required Func<int, bool> Alive { get; init; }
    public Func<int, PolSocialSnapshot?> Read { get; init; } = SocialSnapshotFiles.TryRead;
    public Func<TimeSpan, Task> Delay { get; init; } = t => Task.Delay(t);
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    public static readonly TimeSpan Untaken = TimeSpan.FromSeconds(110);
    // polcore gives up on a job after 100 secs, so a hook that's said nothing by now isn't going to
    public static readonly TimeSpan Working = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    // a session that's gone just lets Play go on: its answer says what happened better than anything here could
    public async Task<PreLaunchResult> RunAsync()
    {
        if (Pid() is not { } pid) return PreLaunchResult.Play;
        var since = Now();
        while (PageWaiting() is { } note)
        {
            if (!Alive(pid)) return PreLaunchResult.Play;
            if (Now() - since >= Untaken)
            {
                if (Read(pid) is not { } now || For(now) is null) return PreLaunchResult.Play;
                Failed(FailedText(PageGaveUpText));
                return PreLaunchResult.Stop;
            }
            Saying?.Invoke(note);
            await Delay(Tick);
        }
        if (Read(pid) is not { } snap || For(snap) is not { } plan) return PreLaunchResult.Play;
        // the viewer's No asks once more, and neither answer starts the game
        if (plan.Ask && !Ask(string.Format(AskText, plan.Active.Name)))
            return Ask(ShowListText) ? PreLaunchResult.ShowHandles : PreLaunchResult.Stop;
        if (plan.Fit.Count > 0)
        {
            var (gone, failed) = await SaveAsync(pid, plan);
            if (gone) return PreLaunchResult.Play;
            if (failed != null)
            {
                Failed(FailedText(failed));
                return PreLaunchResult.Stop;
            }
        }
        if (plan.Fit.Count < plan.Unlinked.Count && Alive(pid))
            return Ask(string.Format(FullText, plan.Active.Name)) ? PreLaunchResult.ShowHandles : PreLaunchResult.Stop;
        return PreLaunchResult.Play;
    }

    private static string Failure(string state) =>
        state == "failed:-7168" ? AlreadyLinkedText : HandleList.Failed(HandleChange.Link, state);

    // Failed is null once it's saved. Once the hook has it, only its answer (or its 150 secs) ends the wait
    private async Task<(bool Gone, string? Failed)> SaveAsync(int pid, Plan plan)
    {
        if (!Alive(pid)) return (true, null);
        var token = HookFiles.NewToken();
        if (!SocialRequests.LinkAll(pid, token, plan.Active, plan.Fit)) return (false, "Provenance couldn't pass the change to PlayOnline.");
        Saying?.Invoke(SavingText);
        var at = Now();
        DateTime? taken = null;
        while (true)
        {
            await Delay(Tick);
            if (Pid() != pid || !Alive(pid)) return (true, null);
            var snap = Read(pid);
            var state = snap is not null && snap.HandleToken == token ? snap.HandleState : "";
            if (state == "saved") return (false, null);
            if (state.StartsWith("failed", StringComparison.Ordinal)) return (false, Failure(state));
            if (state == "working") taken ??= Now();
            if (taken is { } t)
            {
                if (Now() - t >= Working) return (false, Failure("timeout"));
            }
            else if (Now() - at >= Untaken)
            {
                if (HookFiles.Withdraw(pid, HookContract.Requests.Handle) == Withdrawal.Withdrawn) return (false, Failure("timeout"));
                taken = Now();
            }
        }
    }
}
