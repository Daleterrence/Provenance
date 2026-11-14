using Serilog;
using Provenance.Common.Credentials;
using Provenance.Common.Game;
using Provenance.Common.Game.ThirdParty;
using Provenance.Common.Game.Windower;
using Provenance.Common.Install;
using Provenance.Common.Patcher;
using Provenance.Common.PolFormat;
using Provenance.Common.Settings;
using Provenance.Common.Social;

namespace Provenance.Common.Login;

public sealed record ClaimResult(string? Otp, string? InUse, bool OtpCancelled)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Otp = ***");
        return true;
    }
}

public sealed record LaunchLogin(Account Account, PolAccount Pol, SquareEnixAccount SquareEnix, string Otp, int HomeSlot, bool Lean,
                                 string? ThirdPartyHookProfile, IProgress<LaunchProgress>? Progress, Func<string?>? AskHandleName,
                                 Func<bool>? BeforePol = null)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Otp = ***");
        return true;
    }
}

public sealed record LogInResult(int Code, int? Refusal);

// Code and Text are what a login gives, Snapshot the first one that's full and online
public sealed record GoFullResult(int Code, string? Text, PolSocialSnapshot? Snapshot = null);

public sealed record GameUpdateResult(bool Done, bool Cancelled, Exception? Error);

// the game's version check, made once for a start: what the server said, or why it couldn't be asked
public sealed record GameCheck(VersionCheck? Version, Exception? Error)
{
    public bool UpToDate => Version is { UpToDate: true };
}

public sealed record PlaySignal(bool Sent, string? KeyProblem, Exception? Error = null);

public sealed class GameLaunch(PolSession pol, ISettingsService settings)
{
    public static readonly TimeSpan PlayAnswerTimeout = TimeSpan.FromSeconds(15);

    internal Func<(string Ffxi, string Viewer, PolRegion Region)?>? FolderSeam { get; init; }
    internal Func<string, string, PolRegion, VersionCheck?, IProgress<PatchProgress>?, CancellationToken, PatchResult>? UpdateSeam { get; init; }
    internal Func<string, string, PolRegion, IProgress<PatchProgress>?, CancellationToken, VersionCheck>? CheckSeam { get; init; }
    internal Func<CancellationToken, Task<GoFullResult>>? GoFullSeam { get; init; }
    internal Action? SignalPlaySeam { get; init; }
    internal Action<string?>? WriteOptionsSeam { get; init; }
    internal Func<LaunchLogin, CancellationToken, Task<int>>? LogInSeam { get; init; }
    internal Func<Action<string>?, CancellationToken, Task<string?>>? FinishWindowerUpdateSeam { get; init; }

    // claimed before the code's asked, so no other copy logs this account in meanwhile
    public ClaimResult Claim(Account p, Func<string?> askOtp)
    {
        if (!pol.Records.TryClaimAccount(p.Id, out var problem))
            return new(null, problem ?? "This account is in use in another copy of Provenance.", false);
        if (!p.UseOtp) return new("", null, false);
        if (askOtp() is not { } otp)
        {
            pol.Records.ReleaseClaim();
            return new(null, null, true);
        }
        return new(otp, null, false);
    }

    public async Task<LogInResult> LogInAsync(LaunchLogin l, CancellationToken ct)
    {
        int code = LogInSeam is { } seam ? await seam(l, ct) : await pol.ConnectAsync(new LoginRequest(l.Pol, l.SquareEnix, l.Otp)
        {
            ProfileId = l.Account.Id,
            Progress = l.Progress,
            AskHandleName = l.AskHandleName,
            BeforePol = l.BeforePol,
            Options = new SessionOptions(l.Lean, l.ThirdPartyHookProfile, l.HomeSlot, HandleKey: l.Account.HandleKey),
        }, ct);
        return new(code, pol.LastRefusalReason);
    }

    public (string Ffxi, string Viewer, PolRegion Region)? GameFolders() =>
        FolderSeam is { } seam ? seam()
        : PolPaths.TryGetInstallFolder(settings.RegionOrDefault, GameProduct.Game) is { } ffxi
          && PolPaths.TryGetInstallFolder(settings.RegionOrDefault, GameProduct.Viewer) is { } viewer
            ? (ffxi, viewer, settings.RegionOrDefault) : null;

    // known is this start's check, so SE's asked once. One that failed is the update's failure, with nothing asked again
    public async Task<GameUpdateResult> UpdateGameAsync(IProgress<PatchProgress>? progress, CancellationToken ct, GameCheck? known = null)
    {
        if (GameFolders() is not { } dirs) return new(true, false, null);
        if (known?.Error is { } failed) return new(false, false, failed);
        var update = UpdateSeam ?? PolUpdates.UpdateFfxi;
        try
        {
            await Task.Run(() => update(dirs.Ffxi, dirs.Viewer, dirs.Region, known?.Version, progress, ct), ct);
            return new(true, false, null);
        }
        catch (OperationCanceledException ex) { return new(false, true, ex); }
        catch (Exception ex) { return new(false, false, ex); }
    }

    // on the caller's thread. Null with no game to check, or when it was cancelled
    public GameCheck? CheckGame(IProgress<PatchProgress>? progress, CancellationToken ct)
    {
        if (GameFolders() is not { } dirs) return null;
        try { return new((CheckSeam ?? PolUpdates.CheckFfxi)(dirs.Ffxi, dirs.Viewer, dirs.Region, progress, ct), null); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { return new(null, ex); }
    }

    // the game mustn't start while Windower's update is still writing in its folder. Says what's going on only if it has
    // to wait, and gives back the update's notice if it didn't all go in
    public async Task<string?> FinishWindowerUpdateAsync(Action<string>? saying, CancellationToken ct = default)
    {
        if (FinishWindowerUpdateSeam is { } seam) return await seam(saying, ct);
        if (pol.Platform != ThirdPartyHook.Windower || pol.Windower.Update is not { } update) return null;
        if (!update.Finished)
        {
            saying?.Invoke(WindowerLauncher.FinishingUpdateText);
            if (!await update.WaitAsync(ct)) Log.Warning("Windower's update is still going past its limit, the game starts anyway");
        }
        return update.TakeNotice();
    }

    public Task<GoFullResult> GoFullAsync(CancellationToken ct) => GoFullSeam is { } seam ? seam(ct) : pol.GoFullAsync(ct);

    // where the viewer does it, after the update and right before the game starts
    public Task<PreLaunchResult> LinkAsync(Func<string, bool> ask, Action<string> failed, Action<string> saying, Func<string?> pageWaiting,
                                           Func<int, bool>? alive, Func<int, PolSocialSnapshot?> read) =>
        new PreLaunchLink
        {
            Pid = () => pol.Pid,
            Ask = ask,
            Failed = failed,
            Saying = saying,
            PageWaiting = pageWaiting,
            Alive = alive ?? ProcessStamp.PidRunning,
            Read = read,
        }.RunAsync();

    // SE's installer only writes 3 of the game settings
    public void WriteOptions(string? profileId)
    {
        if (WriteOptionsSeam is { } seam)
        {
            seam(profileId);
            return;
        }
        FfxiSettings.WriteFirstStartSettings(settings.RegionOrDefault);
        if (PolPaths.TryGetInstallFolder(settings.RegionOrDefault, GameProduct.Viewer) is { } viewer) WritePolOptions(profileId, viewer);
    }

    // the game reads these from the account's option.bin when it starts, so they go in before every start
    internal void WritePolOptions(string? profileId, string viewer)
    {
        try
        {
            if (settings.Current.Profiles.FirstOrDefault(p => p.Id == profileId) is not { } profile)
            {
                Log.Warning("The session's account is gone, its PlayOnline settings aren't written");
                return;
            }
            var s = settings.Current;
            using (settings.HoldLock())
            {
                // another copy may have changed them since this one read settings.json
                if (settings.ReadSaved() is { } saved)
                {
                    s.ChatLanguageFilter = saved.ChatLanguageFilter ?? s.ChatLanguageFilter;
                    s.AutoDisconnectMinutes = saved.AutoDisconnectMinutes ?? s.AutoDisconnectMinutes;
                }
            }
            PolOptions.Apply(s, viewer, PolOptions.SlotInUse(viewer, profile.HomeSlot), settings.RegionOrDefault);
        }
        catch (Exception e) { Log.Warning(e, "Could not write the PlayOnline settings before the game starts"); }
    }

    // right before the game starts. However it goes the game still starts, a failed one only gets a problem to show
    public async Task<string?> BackUpAsync(string? accountBoot, Action<string>? saying, Func<LauncherSettings, string?, BackupSources?>? sources = null)
    {
        var s = settings.ReadSaved() ?? settings.Current;
        if (!s.BackupGameSettings) return null;
        saying?.Invoke(GameBackupPolicy.WorkingText);
        var region = settings.RegionOrDefault;
        var pick = sources ?? ((cfg, boot) => GameBackupPolicy.SourcesFor(cfg, region, boot));
        return await Task.Run(() => GameBackupPolicy.BeforePlay(s, () => pick(s, accountBoot)));
    }

    public PlaySignal SignalPlay()
    {
        try
        {
            (SignalPlaySeam ?? pol.SignalPlay)();
            return new(true, null);
        }
        catch (Exception ex)
        {
            return new(false, ex is CredentialKeyUnavailableException ? ex.Message : null, ex);
        }
    }

    public Task<string?> WaitForPlayAnswerAsync() => pol.WaitForPlayAnswerAsync(PlayAnswerTimeout);
}
