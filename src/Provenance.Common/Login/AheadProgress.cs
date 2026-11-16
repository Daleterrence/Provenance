namespace Provenance.Common.Login;

// an early start's lines wait here for the login that takes it, then come out in the order they went in
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

    // under the lock, so a line from the early start's own thread can't get in front of the held ones
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
