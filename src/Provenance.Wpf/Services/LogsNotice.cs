namespace Provenance.Wpf.Services;

internal static class LogsNotice
{
    internal static string Text(string problem) =>
        "Provenance couldn't set up its logs folder, so nothing will be logged and a bug report won't have a log file. " +
        $"Everything else still works.\n\nWhat went wrong: {problem}";

    internal static void Tell(IDialogs dialogs, string? problem)
    {
        if (problem is null) return;
        dialogs.Info("Logs aren't being saved", Text(problem));
    }
}
