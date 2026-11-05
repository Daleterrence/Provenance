namespace Provenance.Wpf.Services;

// remade on the next use after CredentialKey.Generation moves, or when keep says no (a faulted task)
internal sealed class WrappedStoreCache<T>(Func<T> make, Func<T, bool>? keep = null) where T : class
{
    private readonly object _gate = new();
    private T? _value;
    private int _generation;

    public T Get()
    {
        lock (_gate)
        {
            // read before the make, so a change during it leaves the value a generation behind for next time
            int generation = CredentialKey.Generation;
            if (_value is { } v && _generation == generation && (keep?.Invoke(v) ?? true)) return v;
            var made = make();
            _value = made;
            _generation = generation;
            return made;
        }
    }

    public void Reset()
    {
        lock (_gate) _value = null;
    }
}
