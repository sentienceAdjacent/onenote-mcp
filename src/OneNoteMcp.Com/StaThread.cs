using System.Collections.Concurrent;

namespace OneNoteMcp.Com;

/// <summary>
/// One dedicated STA thread that runs queued work in order (AD-2). The thread waits with
/// <see cref="WaitHandle.WaitOne()"/>, which the CLR implements as a pumping wait on an STA thread,
/// so COM messages are pumped while the queue is idle.
/// </summary>
internal sealed class StaThread : IDisposable
{
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;

    public StaThread(string name)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public int ManagedThreadId => _thread.ManagedThreadId;

    /// <summary>Queues <paramref name="work"/> on the STA thread. Exceptions it throws fault the task.</summary>
    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }

            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        _signal.Set();
        return tcs.Task;
    }

    /// <summary>Drains work already queued, then stops the thread.</summary>
    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _signal.Set();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    private void Loop()
    {
        while (true)
        {
            while (_queue.TryDequeue(out var work))
            {
                work();
            }

            if (_stopping)
            {
                return;
            }

            _signal.WaitOne();
        }
    }
}
