using Provenance.Common.Game.ThirdParty;

namespace Provenance.Common.Login;

public sealed record PlayRequest(string Handle, ThirdPartyHook? Platform, string? Profile);

public static class CommandLine
{
    public const string BothPlatforms = "Choose either --windower or --ashita, not both.";

    public const string NeedsPlay = "Add --play \"<handle name>\" to say which account to log in.";

    public static string NeedsName(string flag) => $"{flag} needs a name after it.";

    public static string UnknownSwitch(string name) =>
        $"Provenance doesn't know the switch {name}. To log in from the command line, use --play \"<handle name>\" with --windower or --ashita.";

    public static PlayRequest? Parse(IReadOnlyList<string> args, out string? problem, IReadOnlyCollection<string>? appSwitches = null)
    {
        problem = null;
        string? handle = null, profile = null;
        bool ours = false, windower = false, ashita = false;
        for (int i = 0; i < args.Count; i++)
        {
            var (name, inline) = Split(args[i]);
            switch (name)
            {
                case "--play" or "--profile":
                    ours = true;
                    var value = (inline ?? (i + 1 < args.Count && !IsSwitch(args[i + 1]) ? args[++i] : null))?.Trim();
                    if (string.IsNullOrEmpty(value))
                    {
                        problem = NeedsName(name);
                        return null;
                    }
                    if (name == "--play") handle = value;
                    else profile = value;
                    break;
                case "--windower":
                    ours = windower = true;
                    break;
                case "--ashita":
                    ours = ashita = true;
                    break;
                default:
                    if ((args[i].StartsWith('-') || args[i].StartsWith('/')) && appSwitches?.Contains(name, StringComparer.OrdinalIgnoreCase) != true)
                    {
                        problem = UnknownSwitch(args[i].Split('=', 2)[0]);
                        return null;
                    }
                    break;
            }
        }
        if (!ours) return null;
        problem = handle is null ? NeedsPlay : windower && ashita ? BothPlatforms : null;
        if (problem is not null) return null;
        return new PlayRequest(handle!, windower ? ThirdPartyHook.Windower : ashita ? ThirdPartyHook.Ashita : null, profile);
    }

    private static bool IsSwitch(string arg) => arg.StartsWith("--", StringComparison.Ordinal);

    private static (string Name, string? Value) Split(string arg)
    {
        int eq = IsSwitch(arg) ? arg.IndexOf('=') : -1;
        return eq < 0 ? (arg.ToLowerInvariant(), null) : (arg[..eq].ToLowerInvariant(), arg[(eq + 1)..]);
    }
}
