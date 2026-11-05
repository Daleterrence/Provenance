namespace Provenance.Common.Login;

public static class LaunchEnvironment
{
    // the hook reads POL_* and PROVENANCE_*, so a stray one from the user's environment never reaches it
    public static void Reset(IDictionary<string, string?> env)
    {
        foreach (var k in env.Keys.Where(k => k.StartsWith("POL_", StringComparison.OrdinalIgnoreCase) ||
                                              k.StartsWith("PROVENANCE_", StringComparison.OrdinalIgnoreCase)).ToList())
            env.Remove(k);
    }
}
