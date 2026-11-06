using Provenance.Common.Hook;

namespace Provenance.Common.Login;

public static class LaunchEnvironment
{
    // the hook reads POL_* and PROVENANCE_*, so a stray one from the user's environment never reaches it
    public static void Reset(IDictionary<string, string?> env)
    {
        foreach (var k in env.Keys.Where(k => k.StartsWith(HookContract.Env.PolPrefix, StringComparison.OrdinalIgnoreCase) ||
                                              k.StartsWith(HookContract.Env.ProvenancePrefix, StringComparison.OrdinalIgnoreCase)).ToList())
            env.Remove(k);
    }
}
