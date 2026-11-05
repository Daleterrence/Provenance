namespace Provenance.Common;

// what an error says on screen. Mine's written for the player, Windows' and .NET's own wording only goes in the log
public static class ErrorText
{
    public static string Of(Exception ex, string fallback) => Marked(ex)?.Message ?? fallback;

    internal static bool Ours(Exception ex) => Marked(ex) is not null;

    // only what's marked with Shown, and a Task's single wrapped exception counts as itself
    private static Exception? Marked(Exception ex) =>
        Shown.Is(ex) ? ex : ex is AggregateException { InnerExceptions: [var only] } ? Marked(only) : null;
}
