namespace Provenance.Wpf.ViewModels.Dialogs;

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
