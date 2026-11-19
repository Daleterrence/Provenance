using Provenance.Common.Credentials;
using Provenance.Common.Hook;
using Provenance.Common.Settings;

namespace Provenance.Common.Login;

internal static class HookEnvironment
{
    internal sealed record Targets(string Pol, string SquareEnix, IReadOnlyList<string> OneShot, string? Otp, string Handle, string Play,
                                   string Chat);

    internal static void Reset(IDictionary<string, string?> env)
    {
        foreach (var k in env.Keys.Where(k => k.StartsWith(HookContract.Env.PolPrefix, StringComparison.OrdinalIgnoreCase) ||
                                              k.StartsWith(HookContract.Env.ProvenancePrefix, StringComparison.OrdinalIgnoreCase)).ToList())
            env.Remove(k);
    }

    internal static void SetBase(IDictionary<string, string?> env)
    {
        Reset(env);
        env[HookContract.Env.IpcDir] = HookFiles.RequiredDir;
        env[HookContract.Env.CredKeyfile] = CredentialKey.FilePath;
        env[HookContract.Env.Hold] = "1";
        env[HookContract.Env.Owner] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ScreenSaver.AtStart is { } saver) env[HookContract.Env.Screensaver] = saver;
    }

    // POL_IPC_DIR, POL_CRED_KEYFILE and POL_OWNER come from SetBase, the go file never carries them
    internal static void SetPrestart(IDictionary<string, string?> env, bool loginComing = false)
    {
        SetBase(env);
        env[HookContract.Env.Prestart] = "1";
        if (loginComing) env[HookContract.Env.LoginComing] = "1";
    }

    internal static void SetLogin(IDictionary<string, string?> env, Targets targets, int status, SessionOptions options)
    {
        SetBase(env);
        env[HookContract.Env.Status] = status is 0 or 1 or 4 ? status.ToString() : "0";
        env[HookContract.Env.CredPol] = targets.Pol;
        env[HookContract.Env.CredSea] = targets.SquareEnix;
        env[HookContract.Env.CredOneshot] = string.Join(",", targets.OneShot);
        env[HookContract.Env.CredHandle] = targets.Handle;
        env[HookContract.Env.CredPlay] = targets.Play;
        env[HookContract.Env.CredChat] = targets.Chat;
        if (targets.Otp != null) env[HookContract.Env.CredOtp] = targets.Otp;
        if (options.Lean) env[HookContract.Env.Lean] = "1";
        if (HomeSlots.Valid(options.HomeSlot))
            env[HookContract.Env.Home] = options.HomeSlot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SetHandle(env, options.HandleKey);
    }

    internal static void SetHandle(IDictionary<string, string?> env, string? key)
    {
        if (key is { Length: 11 } && key.All(char.IsAsciiHexDigit)) env[HookContract.Env.Handle] = key;
        else env.Remove(HookContract.Env.Handle);
    }

    internal static void SetLaunchId(IDictionary<string, string?> env, string launchId) =>
        env[HookContract.Env.LaunchId] = launchId;

    internal static IEnumerable<string> GoFileEntries(IDictionary<string, string?> env)
    {
        foreach (var k in HookContract.Env.GoFile)
            if (env.TryGetValue(k, out var v) && v != null) yield return $"{k}={v}";
    }

    internal static void WriteGoFile(int pid, IDictionary<string, string?> env) =>
        HookFiles.Transient(HookFiles.PathFor(pid, HookContract.Files.Go), string.Join("\n", GoFileEntries(env)));

    // never PROVENANCE_HOOK, the watcher's --attach loads the hook
    internal static void ApplyContract(IDictionary<string, string?> target, IDictionary<string, string?> launchEnv, string launchId)
    {
        Reset(target);
        foreach (var kv in launchEnv)
            if (kv.Key.StartsWith(HookContract.Env.PolPrefix, StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                target[kv.Key] = kv.Value;
        target[HookContract.Env.Launch] = "1";
        SetLaunchId(target, launchId);
    }
}
