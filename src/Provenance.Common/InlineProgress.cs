namespace Provenance.Common;

// reports on the calling thread, unlike Progress<T> which posts to the sync context
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
