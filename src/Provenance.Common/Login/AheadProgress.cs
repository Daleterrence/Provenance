namespace Provenance.Common.Login;

internal sealed class AheadProgress : IProgress<LaunchProgress>
{
    private readonly object _gate = new();
    private List<LaunchProgress>? _held = [];
    private IProgress<LaunchProgress>? _to;

    public void Report(LaunchProgress value)
    {
        lock (_gate)
        {
            if (_held is { } held) held.Add(value);
            else _to?.Report(value);
        }
    }

    internal void Attach(IProgress<LaunchProgress>? to)
    {
        lock (_gate)
        {
            if (_held is not { } held) return;
            _held = null;
            _to = to;
            foreach (var value in held) to?.Report(value);
        }
    }
}
