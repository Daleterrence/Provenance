namespace Provenance.Common;

public static class ErrorText
{
    public static string Of(Exception ex, string fallback) => Marked(ex)?.Message ?? fallback;

    public static bool Ours(Exception ex) => Marked(ex) is not null;

    private static Exception? Marked(Exception ex) =>
        Shown.Is(ex) ? ex : ex is AggregateException { InnerExceptions: [var only] } ? Marked(only) : null;
}
