using System.ComponentModel;
using Provenance.Common.Credentials;
using Provenance.Common.IO;
using Serilog;

namespace Provenance.Common.Login;

public sealed class LaunchCredentials
{
    private const string LaunchKeyPrefix = "LAUNCH:";
    private const string HandleKind = "HANDLE";
    private const string PlayKind = "PLAY";
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(10);

    // the room password's one-shot entry, and the full target the hook gets as POL_CRED_CHAT
    internal static string ChatKey(string launchId) => $"{LaunchKeyPrefix}{launchId}:CHAT";
    internal static string ChatTarget(string launchId) => Target(ChatKey(launchId));

    private readonly WindowsCredentialStore _store;
    private readonly Func<WindowsCredentialStore> _wrapped;
    private readonly Action? _dropKey;
    private readonly System.Threading.Timer _sweepTimer;
    private readonly HashSet<string> _usedLaunchIds = [];
    private readonly object _ids = new();
    private volatile bool _sweepsStopped;

    public LaunchCredentials() : this(CredentialKey.Keyless, CredentialKey.Wrapped, SweepInterval, CredentialKey.Reload) { }

    internal LaunchCredentials(WindowsCredentialStore keyless, Func<WindowsCredentialStore> wrapped, TimeSpan sweepEvery,
                               Action? dropKey = null)
    {
        _store = keyless;
        _wrapped = wrapped;
        _dropKey = dropKey;
        DeleteLeftovers();
        _sweepTimer = new System.Threading.Timer(_ => SweepLeftovers(), null, sweepEvery, sweepEvery);
    }

    internal string? LaunchId { get; private set; }

    internal Action? OnSweep { get; init; }

    private WindowsCredentialStore Wrapped => _wrapped();

    // the key's opened before anything starts, so one that can't be read gets its own text
    internal string? KeyForLogIn()
    {
        try
        {
            _ = Wrapped;
            return null;
        }
        catch (CredentialKeyUnavailableException) { return CredentialKey.UnavailableText; }
    }

    internal void DropKey()
    {
        try { _dropKey?.Invoke(); }
        catch (Exception ex) { Log.Warning(ex, "Could not read the key file again after the hook could not read an entry"); }
    }

    private void SweepLeftovers()
    {
        if (_sweepsStopped) return;
        OnSweep?.Invoke();
        DeleteLeftovers();
    }

    private void DeleteLeftovers()
    {
        try
        {
            int swept = _store.DeleteByPrefix(LaunchKeyPrefix, LeftoverAge);
            if (swept > 0) Log.Warning("Removed {Count} leftover one-shot sign-in credential(s)", swept);
        }
        catch (Exception ex) { Log.Warning(ex, "Sweep of leftover sign-in credentials failed"); }
    }

    // no sweep touches the store once this returns
    internal void StopSweeps()
    {
        _sweepsStopped = true;
        try
        {
            using var sweepsDone = new ManualResetEvent(false);
            if (_sweepTimer.Dispose(sweepsDone)) sweepsDone.WaitOne();
        }
        catch (Exception ex) { Log.Warning(ex, "Stopping the sweep of leftover sign-in credentials failed"); }
    }

    internal string NewLaunchId()
    {
        var id = ShortId.New(12);
        lock (_ids) _usedLaunchIds.Add(id);
        LaunchId = id;
        return id;
    }

    internal void ForgetLaunch() => LaunchId = null;

    internal void DeleteLaunches()
    {
        string[] ids;
        lock (_ids) { ids = _usedLaunchIds.ToArray(); _usedLaunchIds.Clear(); }
        foreach (var id in ids)
        {
            try { _store.DeleteByPrefix($"{LaunchKeyPrefix}{id}:"); }
            catch (Exception ex) { Log.Warning(ex, "Removing the one-shot sign-in credentials of a launch failed"); }
        }
    }

    private string Key(string kind) => $"{LaunchKeyPrefix}{LaunchId}:{kind}";

    private string TargetOf(string kind) => Target(Key(kind));

    internal static string Target(string key) => $"{WindowsCredentialStore.DefaultPrefix}:{key}";

    // one the key can't read never matches, so what was typed goes as one-shot copies instead
    private string? SavedUserName(string key)
    {
        try { return Wrapped.ReadUserName(key); }
        catch (CredentialUnreadableException) { return null; }
    }

    private string SaveOneShot(string kind, string user, string secret, List<string> keys)
    {
        var key = Key(kind);
        Wrapped.Save(key, user, secret, sessionOnly: true);
        keys.Add(key);
        return Target(key);
    }

    // the account's saved entries when they're still this request's, otherwise one-shot copies of what was typed
    internal HookEnvironment.Targets TargetsFor(LoginRequest request, List<string> keys)
    {
        string polTarget, squareEnixTarget;
        var oneShot = new List<string> { "OTP" };
        var profile = string.IsNullOrWhiteSpace(request.ProfileId) ? null : request.ProfileId;
        if (profile != null && SavedUserName($"{CredentialTargets.Pol}:{profile}") == request.Pol.PolId &&
            SavedUserName($"{CredentialTargets.Sea}:{profile}") == request.SquareEnix.Username)
        {
            polTarget = Target($"{CredentialTargets.Pol}:{profile}");
            squareEnixTarget = Target($"{CredentialTargets.Sea}:{profile}");
        }
        else
        {
            polTarget = SaveOneShot("POL", request.Pol.PolId, request.Pol.Password, keys);
            squareEnixTarget = SaveOneShot("SEA", request.SquareEnix.Username, request.SquareEnix.Password, keys);
            oneShot.InsertRange(0, ["POL", "SEA"]);
        }
        var otpTarget = string.IsNullOrWhiteSpace(request.Otp) ? null : SaveOneShot("OTP", "otp", request.Otp.Trim(), keys);
        keys.AddRange([Key(HandleKind), Key(PlayKind)]);
        return new HookEnvironment.Targets(polTarget, squareEnixTarget, oneShot, otpTarget, TargetOf(HandleKind), TargetOf(PlayKind),
                                           ChatTarget(LaunchId!));
    }

    internal void SaveHandle(string? name) =>
        Wrapped.Save(Key(HandleKind), "handle", string.IsNullOrWhiteSpace(name) ? "" : name!.Trim(), sessionOnly: true);

    internal void CleanupOneShots(List<string> keys)
    {
        foreach (var k in keys)
        {
            try { _store.Delete(k); } catch (Win32Exception) { }
        }
        keys.Clear();
    }

    // the room password goes to the hook as a one-shot entry like the account details, deleted once read.
    // User is the enter's token, so it only works for that one
    public bool SaveChatPassword(string token, string password)
    {
        if (LaunchId is not { } id) return false;
        try
        {
            Wrapped.Save(ChatKey(id), token, password, sessionOnly: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not hand the room password to the hook");
            return false;
        }
    }

    public void DropChatPassword()
    {
        if (LaunchId is { } id)
            try { _store.Delete(ChatKey(id)); } catch (Win32Exception) { }
    }

    internal void SavePlay() => Wrapped.Save(Key(PlayKind), "play", "go", sessionOnly: true);

    internal void DeletePlay()
    {
        if (LaunchId is null) return;
        try { _store.Delete(Key(PlayKind)); }
        catch (Exception ex) { Log.Warning(ex, "Could not remove the Play entry of the game that did not start"); }
    }
}
