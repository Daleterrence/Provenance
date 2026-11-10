namespace Provenance.Wpf.ViewModels.Dialogs;

// Only the newest piece of work counts, starting another cancels the last. A token that says cancelled is a stale one
internal sealed class LatestOnly
{
    private CancellationTokenSource? _last;

    public CancellationToken Begin()
    {
        Cancel();
        _last?.Dispose();
        return (_last = new CancellationTokenSource()).Token;
    }

    public void Cancel() => _last?.Cancel();
}
