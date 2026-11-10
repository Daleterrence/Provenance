#pragma warning disable RS0030 // app-level lookups live in the services
using System.Windows;
using System.Windows.Threading;

namespace Provenance.Wpf.Services;

// The UI thread taken when this is made. Post, unless the caller needs the answer or needs it done by the time it
// returns, then Run. Nothing runs once the app's gone
public sealed class UiThread
{
    private readonly Dispatcher? _dispatcher = Application.Current?.Dispatcher;

    public bool Available => _dispatcher is not null;

    public void Post(Action action) => _dispatcher?.InvokeAsync(action);

    public void Post(Func<Task> action) => _dispatcher?.InvokeAsync(action);

    public void Run(Action action) => _dispatcher?.Invoke(action);

    public T? Run<T>(Func<T> func) => _dispatcher is null ? default : _dispatcher.Invoke(func);

    // a hop, so progress reports already queued land first
    public Task Flush() => _dispatcher?.InvokeAsync(() => { }).Task ?? Task.CompletedTask;

    public async Task<T> RunAsync<T>(Func<Task<T>> func) =>
        _dispatcher is null ? await func() : await await _dispatcher.InvokeAsync(func);
}
