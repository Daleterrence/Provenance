namespace Provenance.Common.Game.Windower;

// Windower.exe holds Windower.Update.Mutex around every update and install. A mutex belongs to a thread and awaits hop
// threads, so a thread of its own holds this one
internal sealed class WindowerUpdateMutex : IDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private Thread? _thread;

    private WindowerUpdateMutex() { }

    // null when it's still held elsewhere after wait
    public static Task<WindowerUpdateMutex?> AcquireAsync(string name, TimeSpan wait, CancellationToken ct)
    {
        var hold = new WindowerUpdateMutex();
        var ready = new TaskCompletionSource<WindowerUpdateMutex?>(TaskCreationOptions.RunContinuationsAsynchronously);
        hold._thread = new Thread(() =>
        {
            Mutex m;
            try { m = new Mutex(false, name); }
            catch (Exception e)
            {
                ready.SetException(e);
                return;
            }
            bool owned = false;
            try
            {
                // an abandoned mutex is mine now, and gets released once like any other (Windower takes it twice and
                // keeps it till it exits)
                int got;
                try { got = WaitHandle.WaitAny([m, ct.WaitHandle], wait); }
                catch (AbandonedMutexException e) when (e.MutexIndex == 0) { got = 0; }
                owned = got == 0;
                if (got == 1) ready.SetCanceled(ct);
                else if (!owned) ready.SetResult(null);
                else
                {
                    ready.SetResult(hold);
                    hold._release.Wait();
                }
            }
            catch (Exception e) { ready.TrySetException(e); }
            finally
            {
                if (owned) m.ReleaseMutex();
                m.Dispose();
            }
        }) { IsBackground = true, Name = "Windower update mutex" };
        hold._thread.Start();
        return ready.Task;
    }

    public void Dispose()
    {
        _release.Set();
        _thread?.Join(TimeSpan.FromSeconds(5));
    }
}
