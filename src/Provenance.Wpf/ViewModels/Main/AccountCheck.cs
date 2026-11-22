using Provenance.Common;
using Provenance.Common.Credentials;
using Provenance.Common.Login;
using Provenance.Common.Progress;
using Provenance.Common.Settings;
using Provenance.Common.Social;
using Provenance.Wpf.Services;
using Serilog;

namespace Provenance.Wpf.ViewModels.Main;

public sealed record AccountCheckAnswer(bool Finish, string? Problem = null);

public delegate Task<AccountCheckAnswer> AccountChecker(AccountDialogResult typed, CancellationToken ct);

internal sealed record AccountChecked(AccountCheckAnswer Answer, PolSocialSnapshot? Snapshot);

internal sealed class AccountCheck(IDialogs dialogs, PolSession session, GameLaunch launch, RefusedPasswords refused, Func<bool> free,
                                   Func<bool> agreed, Func<Account, SquareEnixAccount, string?> askOtp)
{
    internal const string LockoutTitle = "Check this account";
    internal const string CouldNotTitle = "Couldn't check this account";
    internal const string AddAnywayText = "You can still add this account without the check. Its first login will check the details instead.";

    private static readonly AccountChecked Add = new(new(true), null);
    private static readonly AccountChecked Back = new(new(false), null);

    private PolSocialSnapshot? _seen;

    internal bool Running { get; private set; }

    internal event Action? RunningChanged;

    internal bool Capture(PolSocialSnapshot snap)
    {
        if (!Running) return false;
        _seen = snap;
        return true;
    }

    internal async Task<AccountChecked> RunAsync(Account profile, AccountDialogResult typed, CancellationToken ct)
    {
        if (!free() || !agreed()) return Add;
        try { return await CheckAsync(profile, typed, ct); }
        catch (OperationCanceledException) { return Back; }
        catch (Exception ex)
        {
            Log.Error(ex, "Checking the details of a new account failed");
            return Offer(ErrorText.Of(ex, "Provenance ran into a problem while logging in to check these details."));
        }
    }

    private async Task<AccountChecked> CheckAsync(Account profile, AccountDialogResult typed, CancellationToken ct)
    {
        var pol = new PolAccount(typed.PolId, typed.PolPassword);
        var squareEnix = new SquareEnixAccount(typed.SeaId, typed.SeaPassword);
        if (AccountCheckRules.LockoutQuestion(refused.Count(typed.PolId)) is { } question &&
            !dialogs.Confirm(LockoutTitle, question, "LOG IN", cancel: "GO BACK")) return Back;
        var claim = launch.Claim(profile, () => askOtp(profile, squareEnix));
        if (claim.InUse is not null || claim.OtpCancelled) return Add;
        string? failure = null;
        var progress = new InlineProgress<LaunchProgress>(p =>
        {
            if (p.Kind is LaunchProgressKind.Failed or LaunchProgressKind.Stopped) failure = p.Detail;
        });
        LogInResult result;
        PolSocialSnapshot? snap = null;
        _seen = null;
        Running = true;
        RunningChanged?.Invoke();
        try
        {
            result = await launch.LogInAsync(new LaunchLogin(profile, pol, squareEnix, claim.Otp ?? "", 0, true, null, progress, () => null, Plain: true), ct);
            if (result.Code == 0) snap = _seen ?? (session.Pid is { } pid ? SocialSnapshotFiles.TryRead(pid) : null);
        }
        finally
        {
            await Task.Run(() =>
            {
                session.StopHold();
                session.Prestarter.Prestart();
            });
            _seen = null;
            Running = false;
            RunningChanged?.Invoke();
        }
        if (ct.IsCancellationRequested) return Back;
        switch (AccountCheckRules.Judge(result.Code, result.Refusal))
        {
            case AccountCheckVerdict.Accepted:
                refused.Clear(typed.PolId);
                return new(new(true), snap);
            case AccountCheckVerdict.Cancelled:
                return Back;
            case AccountCheckVerdict.Refused:
                refused.Add(typed.PolId);
                return new(new(false, failure ?? HookExitText.RefusedText), null);
            case AccountCheckVerdict.Locked:
                refused.MarkLocked(typed.PolId);
                return Offer(failure ?? PolAccountNotices.LogInRefused(-205));
            default:
                return Offer(failure ?? $"Provenance couldn't log in to PlayOnline (code {result.Code}).");
        }
    }

    private AccountChecked Offer(string why) =>
        dialogs.Confirm(CouldNotTitle, why + "\n\n" + AddAnywayText, "ADD ACCOUNT", cancel: "GO BACK") ? Add : Back;
}
