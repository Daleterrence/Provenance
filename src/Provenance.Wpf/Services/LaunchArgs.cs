namespace Provenance.Wpf.Services;

// never saved to settings. --account <profile id> uses that account, --play signs in at once and starts the game.
public static class LaunchArgs
{
    public static string? ProfileId { get; private set; }
    public static bool Play { get; private set; }

    public static void Parse(string[] argv)
    {
        for (int i = 0; i < argv.Length; i++)
        {
            string? Next() => i + 1 < argv.Length ? argv[++i] : null;
            switch (argv[i].ToLowerInvariant())
            {
                case "--account": ProfileId = Next(); break;
                case "--play": Play = true; break;
            }
        }
    }
}
