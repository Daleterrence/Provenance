namespace Provenance.Wpf.ViewModels.Settings;

internal sealed class CardWork
{
    internal static bool InlineForTest;

    private Task _tail = Task.CompletedTask;

    internal Task Tail => _tail;

    internal Func<Func<object?>, Task<object?>>? RunForTest { get; set; }

    internal Task<T> Run<T>(Func<T> work, Action<T>? done = null)
    {
        var prev = _tail;
        var task = Go();
        _tail = task;
        return task;

        async Task<T> Go()
        {
            try { await prev; }
            catch { }
            T result;
            if (RunForTest is { } run) result = (T)(await run(() => work()))!;
            else if (InlineForTest) result = work();
            else result = await Task.Run(work);
            done?.Invoke(result);
            return result;
        }
    }
}
